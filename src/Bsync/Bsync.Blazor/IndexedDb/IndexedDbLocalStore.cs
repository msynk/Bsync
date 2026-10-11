using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Bsync.Clocks;
using Bsync.Storage;
using Microsoft.JSInterop;

namespace Bsync.Blazor.IndexedDb;

/// <summary>
/// A durable <see cref="ILocalStore{TDocument}"/> in the browser's IndexedDB, through a small JavaScript
/// module (<c>_content/Bsync.Blazor/bsync-indexeddb.js</c>).
/// </summary>
/// <remarks>
/// <para>
/// Updates are optimistic: records are read with a write stamp, the transforms run in .NET, and the new
/// states, cursor and clock high-water mark are committed in one readwrite IndexedDB transaction only if no
/// stamp changed; otherwise the update is retried. No transaction spans a .NET await, and concurrent tabs
/// can never commit over each other's newer state. Commits request <c>durability: "strict"</c>.
/// </para>
/// <para>
/// Browsers may evict storage under pressure unless the origin was granted persistence
/// (<see cref="RequestPersistenceAsync"/>), and private browsing modes may discard it when the session
/// ends. Failures surface as <see cref="LocalStoreUnavailableException"/>, never as silent success.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed partial class IndexedDbLocalStore<TDocument> : ILocalStore<TDocument>, IAsyncDisposable
    where TDocument : class, ISyncEntity
{
    private const string ModulePath = "./_content/Bsync.Blazor/bsync-indexeddb.js";

    private readonly IJSObjectReference _module;
    private readonly int _handle;
    private readonly string _collection;
    private readonly JsonTypeInfo<TDocument> _typeInfo;
    private readonly int _maxAttempts;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly IReadOnlyList<SyncIndex<TDocument>> _indexes;
    private readonly string _indexSignature;
    private int _disposed;

    private IndexedDbLocalStore(IJSObjectReference module, int handle, IndexedDbStoreOptions options, JsonTypeInfo<TDocument> typeInfo, IReadOnlyList<SyncIndex<TDocument>> indexes)
    {
        _indexes = indexes;
        _indexSignature = LocalStoreIndexing.Signature(indexes);
        _module = module;
        _handle = handle;
        _collection = options.Collection;
        _typeInfo = typeInfo;
        _maxAttempts = options.MaxCommitAttempts;
    }

    /// <summary>Opens (creating if needed) the store. Call only after the browser runtime is interactive.</summary>
    /// <exception cref="LocalStoreUnavailableException">IndexedDB is unavailable, blocked or newer than this application.</exception>
    public static Task<IndexedDbLocalStore<TDocument>> OpenAsync(
        IJSRuntime js,
        IndexedDbStoreOptions options,
        JsonTypeInfo<TDocument> typeInfo,
        CancellationToken cancellationToken = default) =>
        OpenAsync(js, options, typeInfo, [], cancellationToken);

    /// <summary>
    /// Opens (creating if needed) a store that maintains <paramref name="indexes"/> (ADR-018); keys of existing records
    /// are built here when the declared set changed. Every tab's store of a collection should declare the same indexes:
    /// a writer with another set marks the keys unusable, and queries are then evaluated in memory until a store with
    /// the right set opens again. Call only after the browser runtime is interactive.
    /// </summary>
    /// <exception cref="LocalStoreUnavailableException">IndexedDB is unavailable, blocked or newer than this application.</exception>
    public static async Task<IndexedDbLocalStore<TDocument>> OpenAsync(
        IJSRuntime js,
        IndexedDbStoreOptions options,
        JsonTypeInfo<TDocument> typeInfo,
        IEnumerable<SyncIndex<TDocument>> indexes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseName);
        if (!SyncIds.IsValid(options.Collection))
        {
            throw new ArgumentException("The collection name must be a valid identifier.", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxCommitAttempts, 1, nameof(options.MaxCommitAttempts));

        var module = await ImportAsync(js, cancellationToken).ConfigureAwait(false);
        if (options.EncryptionKey is { Length: not 32 })
        {
            throw new ArgumentException("An encryption key is 32 bytes (256 bits).", nameof(options));
        }

        var key = options.EncryptionKey is { } bytes ? Convert.ToBase64String(bytes) : null;
        var handle = await Call(() => module.InvokeAsync<int>("open", cancellationToken, options.DatabaseName, (int)options.BlockedTimeout.TotalMilliseconds, key)).ConfigureAwait(false);
        var store = new IndexedDbLocalStore<TDocument>(module, handle, options, typeInfo, LocalStoreIndexing.Validate(indexes));
        await store.RebuildIndexesAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    /// <summary>Deletes a whole database (all collections). For account removal and tests.</summary>
    public static async Task DeleteDatabaseAsync(IJSRuntime js, string databaseName, CancellationToken cancellationToken = default)
    {
        var module = await ImportAsync(js, cancellationToken).ConfigureAwait(false);
        await Call(() => module.InvokeVoidAsync("deleteDatabase", cancellationToken, databaseName)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SyncRecord<TDocument>?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var read = await ReadManyAsync([id], cancellationToken).ConfigureAwait(false);
        return read.Records[0] is { } record ? ToRecord(record) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecordUpdateResult<TDocument>>> UpdateAsync(
        IReadOnlyList<RecordUpdate<TDocument>> updates,
        ReplicaCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        cancellationToken.ThrowIfCancellationRequested();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            ArgumentNullException.ThrowIfNull(update);
            if (!seen.Add(update.Id))
            {
                throw new ArgumentException($"Duplicate record id '{update.Id}' in one update.", nameof(updates));
            }
        }

        // Writers in this tab take turns; only writers in other tabs can make a commit conflict.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CommitWithRetriesAsync(updates, cursor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<IReadOnlyList<RecordUpdateResult<TDocument>>> CommitWithRetriesAsync(
        IReadOnlyList<RecordUpdate<TDocument>> updates,
        ReplicaCursor? cursor,
        CancellationToken cancellationToken)
    {
        var ids = updates.Select(u => u.Id).ToArray();
        for (var attempt = 1; ; attempt++)
        {
            var read = await ReadManyAsync(ids, cancellationToken).ConfigureAwait(false);
            var entries = new List<IdbCommitEntry>(updates.Count);
            var results = new List<RecordUpdateResult<TDocument>>(updates.Count);
            HlcTimestamp? highWater = null;
            for (var i = 0; i < updates.Count; i++)
            {
                var stored = read.Records[i];
                var next = updates[i].Transform(stored is null ? null : ToRecord(stored));
                if (next is null)
                {
                    entries.Add(new IdbCommitEntry(ids[i], null, stored?.Stamp));
                    results.Add(new RecordUpdateResult<TDocument>(stored is null ? null : ToRecord(stored), Changed: false));
                    continue;
                }

                if (!string.Equals(next.Current.Id, ids[i], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Transform for '{ids[i]}' returned a record with id '{next.Current.Id}'.");
                }

                var dto = ToDto(next);
                entries.Add(new IdbCommitEntry(ids[i], dto, stored?.Stamp));
                results.Add(new RecordUpdateResult<TDocument>(ToRecord(dto), Changed: true));
                highWater = Max(highWater, next.Current.UpdatedAt);
                if (next.Pending is { } pending)
                {
                    highWater = Max(highWater, pending.Payload.UpdatedAt);
                }
            }

            var meta = highWater is null && cursor is null
                ? null
                : new IdbMetaUpdate(
                    highWater?.Encode(),
                    cursor is { } c ? new IdbCursor(c.Checkpoint.Value, c.Generation.ToString(CultureInfo.InvariantCulture), c.Resnapshot, c.PurgeMissing) : null);

            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await Call(() => _module.InvokeAsync<string>(
                "commit",
                CancellationToken.None,
                _handle,
                _collection,
                JsonSerializer.Serialize(entries, IdbJsonContext.Default.ListIdbCommitEntry),
                meta is null ? null : JsonSerializer.Serialize(meta, IdbJsonContext.Default.IdbMetaUpdate),
                _indexSignature)).ConfigureAwait(false);

            if (outcome == "ok")
            {
                return results;
            }

            if (attempt >= _maxAttempts)
            {
                throw new LocalStoreUnavailableException("error", $"The update did not commit after {attempt} attempts because other tabs kept changing the same records.");
            }

            // Another tab committed first; back off a little (with jitter) before re-reading.
            await Task.Delay(Random.Shared.Next(1, 5 * attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> GetPendingAsync(int limit, IReadOnlySet<string>? exclude = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var json = await Call(() => _module.InvokeAsync<string>("pending", cancellationToken, _handle, _collection, limit, exclude?.ToArray() ?? [])).ConfigureAwait(false);
        return ToRecords(json);
    }

    /// <inheritdoc />
    public Task<int> CountDirtyAsync(CancellationToken cancellationToken = default) =>
        Call(() => _module.InvokeAsync<int>("countDirty", cancellationToken, _handle, _collection).AsTask());

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> GetStaleAsync(long generation, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var json = await Call(() => _module.InvokeAsync<string>("stale", cancellationToken, _handle, _collection, generation.ToString(CultureInfo.InvariantCulture), limit)).ConfigureAwait(false);
        return ToRecords(json);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> GetConflictsAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var json = await Call(() => _module.InvokeAsync<string>("conflicts", cancellationToken, _handle, _collection, limit)).ConfigureAwait(false);
        return ToRecords(json);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> GetRejectedAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var json = await Call(() => _module.InvokeAsync<string>("rejected", cancellationToken, _handle, _collection, limit)).ConfigureAwait(false);
        return ToRecords(json);
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(IReadOnlyList<string> ids, long generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Call(() => _module.InvokeAsync<int>("purge", CancellationToken.None, _handle, _collection, ids.ToArray(), generation.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TDocument>> QueryAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var json = await Call(() => _module.InvokeAsync<string>("query", cancellationToken, _handle, _collection, includeDeleted)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, IdbJsonContext.Default.ListString)!.Select(Deserialize).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TDocument>> QueryPageAsync(string? afterId, int limit, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var json = await Call(() => _module.InvokeAsync<string>("queryPage", cancellationToken, _handle, _collection, afterId, limit, includeDeleted)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, IdbJsonContext.Default.ListString)!.Select(Deserialize).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TDocument>> QueryIndexAsync(SyncIndexQuery<TDocument> query, SyncIndexCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (Maintains(query.Index))
        {
            var json = await Call(() => _module.InvokeAsync<string>(
                "queryIndex",
                cancellationToken,
                _handle,
                _collection,
                _indexSignature,
                query.Index.Name,
                query.Lower,
                query.LowerExclusive,
                query.Upper,
                query.UpperExclusive,
                after?.Key,
                after?.Id,
                query.IsDescending,
                limit)).ConfigureAwait(false);
            if (json != "null")
            {
                return JsonSerializer.Deserialize(json, IdbJsonContext.Default.ListString)!.Select(Deserialize).ToList();
            }
        }

        return await LocalStoreIndexing.QueryAsync(this, query, after, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountIndexAsync(SyncIndexQuery<TDocument> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Maintains(query.Index))
        {
            var count = await Call(() => _module.InvokeAsync<int>(
                "countIndex", cancellationToken, _handle, _collection, _indexSignature, query.Index.Name, query.Lower, query.LowerExclusive, query.Upper, query.UpperExclusive).AsTask()).ConfigureAwait(false);
            if (count >= 0)
            {
                return count;
            }
        }

        return await LocalStoreIndexing.CountAsync(this, query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The index set the stored keys were built for (tests).</summary>
    internal Task<string> IndexStateAsync() => Call(() => _module.InvokeAsync<string>("indexState", CancellationToken.None, _handle, _collection).AsTask());

    private bool Maintains(SyncIndex<TDocument> index) => _indexes.Any(i => i.Name == index.Name && i.Version == index.Version);

    /// <summary>Builds the keys of existing records when the declared set differs from the one they were built for.</summary>
    private async Task RebuildIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexes.Count == 0)
        {
            return;
        }

        var token = await Call(() => _module.InvokeAsync<string>("beginReindex", cancellationToken, _handle, _collection, _indexSignature).AsTask()).ConfigureAwait(false);
        if (token.Length == 0)
        {
            return;
        }

        string? after = null;
        while (true)
        {
            var json = await Call(() => _module.InvokeAsync<string>("scanLive", cancellationToken, _handle, _collection, after, 200).AsTask()).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize(json, IdbJsonContext.Default.ListIdbLiveRecord)!;
            if (page.Count == 0)
            {
                break;
            }

            var keys = page.Select(r => new IdbIndexKeys(r.Id, r.Stamp, KeysOf(Deserialize(r.Current)))).ToList();
            await Call(() => _module.InvokeVoidAsync("reindex", cancellationToken, _handle, _collection, JsonSerializer.Serialize(keys, IdbJsonContext.Default.ListIdbIndexKeys))).ConfigureAwait(false);
            after = page[^1].Id;
        }

        await Call(() => _module.InvokeAsync<bool>("endReindex", cancellationToken, _handle, _collection, _indexSignature, token).AsTask()).ConfigureAwait(false);
    }

    private Dictionary<string, string> KeysOf(TDocument document)
    {
        var keys = new Dictionary<string, string>(_indexes.Count, StringComparer.Ordinal);
        foreach (var index in _indexes)
        {
            keys[index.Name] = index.KeyOf(document);
        }

        return keys;
    }

    /// <inheritdoc />
    public async Task<ReplicaCursor> GetCursorAsync(CancellationToken cancellationToken = default)
    {
        var meta = await GetMetaAsync(cancellationToken).ConfigureAwait(false);
        return new ReplicaCursor(new Checkpoint(meta.Checkpoint), long.Parse(meta.Generation, CultureInfo.InvariantCulture), meta.Resnapshot, meta.PurgeMissing);
    }

    /// <inheritdoc />
    public async Task<HlcTimestamp> GetClockHighWaterAsync(CancellationToken cancellationToken = default)
    {
        var meta = await GetMetaAsync(cancellationToken).ConfigureAwait(false);
        return meta.HighWater is null ? HlcTimestamp.MinValue : HlcTimestamp.Parse(meta.HighWater);
    }

    /// <inheritdoc />
    public Task ResetClockHighWaterAsync(HlcTimestamp value, CancellationToken cancellationToken = default) =>
        Call(() => _module.InvokeVoidAsync("setHighWater", cancellationToken, _handle, _collection, value.Encode()));

    /// <summary>Returns the database's replica id and current incarnation.</summary>
    public async Task<ReplicaIdentity> GetReplicaIdentityAsync(CancellationToken cancellationToken = default)
    {
        var meta = await GetMetaAsync(cancellationToken).ConfigureAwait(false);
        return new ReplicaIdentity(meta.ReplicaId!, meta.Incarnation!);
    }

    /// <summary>Assigns a new incarnation id (after the database may have been copied or restored).</summary>
    public async Task<ReplicaIdentity> BeginNewIncarnationAsync(CancellationToken cancellationToken = default)
    {
        await Call(() => _module.InvokeVoidAsync("newIncarnation", cancellationToken, _handle)).ConfigureAwait(false);
        return await GetReplicaIdentityAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the browser to exempt this origin's storage from eviction under pressure. Returns whether
    /// persistence is granted. Browsers may prompt the user (Firefox does) or decide heuristically, so the
    /// returned task may not complete until the user answers: do not await it on a startup path.
    /// </summary>
    public Task<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default) =>
        Call(() => _module.InvokeAsync<bool>("requestPersistence", cancellationToken).AsTask());

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await _module.InvokeVoidAsync("close", _handle).ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The page is gone; the browser closes the connection.
            }
        }
    }

    internal static async Task<IJSObjectReference> ImportAsync(IJSRuntime js, CancellationToken cancellationToken) =>
        await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);

    internal static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (JSException error)
        {
            throw Translate(error);
        }
    }

    internal static Task<T> Call<T>(Func<ValueTask<T>> call) => Call(() => call().AsTask());

    internal static async Task Call(Func<ValueTask> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (JSException error)
        {
            throw Translate(error);
        }
    }

    private static Exception Translate(JSException error)
    {
        var match = StoreError().Match(error.Message);
        return match.Success
            ? new LocalStoreUnavailableException(match.Groups[1].Value, match.Groups[2].Value.Trim(), error)
            : new LocalStoreUnavailableException("error", error.Message, error);
    }

    [GeneratedRegex(@"Bsync:([a-z-]+):([^\r\n]*)")]
    private static partial Regex StoreError();

    private static HlcTimestamp Max(HlcTimestamp? a, HlcTimestamp b) => a is { } x && x >= b ? x : b;

    private static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static long? Number(string? value) => value is null ? null : long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private async Task<IdbRead> ReadManyAsync(string[] ids, CancellationToken cancellationToken)
    {
        var json = await Call(() => _module.InvokeAsync<string>("readMany", cancellationToken, _handle, _collection, ids)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, IdbJsonContext.Default.IdbRead)!;
    }

    private async Task<IdbMeta> GetMetaAsync(CancellationToken cancellationToken)
    {
        var json = await Call(() => _module.InvokeAsync<string>("getMeta", cancellationToken, _handle, _collection)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, IdbJsonContext.Default.IdbMeta)!;
    }

    private List<SyncRecord<TDocument>> ToRecords(string json) =>
        JsonSerializer.Deserialize(json, IdbJsonContext.Default.ListIdbRecord)!.Select(ToRecord).ToList();

    private IdbRecord ToDto(SyncRecord<TDocument> record) => new()
    {
        Id = record.Current.Id,
        Current = Serialize(record.Current),
        UpdatedAt = record.Current.UpdatedAt.Encode(),
        Deleted = record.Current.Deleted,
        Base = record.Base is null ? null : Serialize(record.Base),
        BaseVersion = Text(record.BaseVersion),
        IsDirty = record.IsDirty,
        LocalRevision = Text(record.LocalRevision)!,
        PendingId = record.Pending?.OperationId,
        PendingRevision = Text(record.Pending?.Revision),
        PendingBaseVersion = Text(record.Pending?.BaseVersion),
        PendingPayload = record.Pending is null ? null : Serialize(record.Pending.Payload),
        RejectionRevision = Text(record.Rejection?.Revision),
        RejectionCode = record.Rejection?.ErrorCode,
        RejectionMessage = record.Rejection?.Message,
        RejectionArguments = record.Rejection?.Arguments is { Count: > 0 } arguments ? new Dictionary<string, string>(arguments, StringComparer.Ordinal) : null,
        Observed = record.Observed is null ? null : Serialize(record.Observed),
        ObservedVersion = Text(record.ObservedVersion),
        Generation = Text(record.Generation)!,
        Missing = record.MissingAfterReset,
        ConflictServer = record.Conflict is { } c ? Serialize(c.Server) : null,
        ConflictServerVersion = Text(record.Conflict?.ServerVersion),
        ConflictLocal = record.Conflict is { } cl ? Serialize(cl.Local) : null,
        ConflictBase = record.Conflict?.Base is { } cb ? Serialize(cb) : null,
        GroupId = record.Group?.Id,
        GroupMembers = record.Group?.Members.ToList(),
        PendingGroup = record.Pending?.Group,
        PendingGroupSize = record.Pending is { Group: not null } pending ? pending.GroupSize : null,
        IndexKeys = _indexes.Count == 0 || record.Current.Deleted || record.MissingAfterReset ? null : KeysOf(record.Current),
    };

    private SyncRecord<TDocument> ToRecord(IdbRecord dto) =>
        new(Deserialize(dto.Current), dto.Base is null ? null : Deserialize(dto.Base), dto.IsDirty)
        {
            BaseVersion = Number(dto.BaseVersion),
            LocalRevision = Number(dto.LocalRevision) ?? 0,
            Pending = dto.PendingId is null
                ? null
                : new PendingOperation<TDocument>(dto.PendingId, Number(dto.PendingRevision) ?? 0, Number(dto.PendingBaseVersion), Deserialize(dto.PendingPayload!))
                {
                    Group = dto.PendingGroup,
                    GroupSize = dto.PendingGroupSize ?? 0,
                },
            Rejection = dto.RejectionCode is null ? null : new SyncRejection(Number(dto.RejectionRevision) ?? 0, dto.RejectionCode, dto.RejectionMessage) { Arguments = dto.RejectionArguments },
            Observed = dto.Observed is null ? null : Deserialize(dto.Observed),
            ObservedVersion = Number(dto.ObservedVersion),
            Generation = Number(dto.Generation) ?? 0,
            MissingAfterReset = dto.Missing,
            Conflict = dto.ConflictLocal is null
                ? null
                : new SyncConflict<TDocument>(
                    Deserialize(dto.ConflictServer!),
                    Number(dto.ConflictServerVersion) ?? 0,
                    Deserialize(dto.ConflictLocal),
                    dto.ConflictBase is null ? null : Deserialize(dto.ConflictBase)),
            Group = dto.GroupId is null ? null : new SyncGroup(dto.GroupId, dto.GroupMembers ?? []),
        };

    private string Serialize(TDocument document) => JsonSerializer.Serialize(document, _typeInfo);

    private TDocument Deserialize(string json) =>
        JsonSerializer.Deserialize(json, _typeInfo) ?? throw new InvalidDataException("A stored document deserialized to null.");
}
