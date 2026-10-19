using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Client;

/// <summary>A derived row. Payload serialization and its schema belong to the host; credentials must not be stored.</summary>
/// <param name="Id">Unique identity within a generation, at most 256 characters.</param>
/// <param name="Payload">Host-serialized projected data, at most 262144 characters.</param>
public sealed record ReadModelRow(string Id, string Payload);

/// <summary>An immutable, completely applied generation. SourceVersion identifies a host-captured stable snapshot, not current freshness.</summary>
/// <param name="Generation">Opaque local generation.</param>
/// <param name="SchemaVersion">Host projection schema.</param>
/// <param name="SourceVersion">Host source epoch and snapshot boundary.</param>
/// <param name="RowCount">Number of committed rows.</param>
/// <param name="CompletedAt">Activation time, not a source cursor.</param>
public sealed record ReadModelGeneration(string Generation, string SchemaVersion, string SourceVersion, long RowCount, DateTimeOffset CompletedAt);

/// <summary>Durable progress; an interrupted rebuild remains visible and does not replace Active.</summary>
/// <param name="FormatVersion">Library metadata format.</param>
/// <param name="Active">Last complete generation, possibly stale.</param>
/// <param name="BuildingGeneration">Incomplete generation, if any.</param>
/// <param name="AppliedRows">Rows durably committed to the incomplete generation.</param>
public sealed record ReadModelStatus(int FormatVersion, ReadModelGeneration? Active, string? BuildingGeneration, long AppliedRows);

/// <summary>One bounded immutable-generation page. Pass Continuation to the same snapshot only.</summary>
/// <param name="Rows">Derived rows in opaque, deterministic key order.</param>
/// <param name="Continuation">Opaque continuation; null when enumeration is complete.</param>
public sealed record ReadModelPage(IReadOnlyList<ReadModelRow> Rows, string? Continuation);

/// <summary>
/// Full-reconciliation read model over account-scoped metadata. The host must supply every row from one stable source
/// snapshot (including a source epoch), obtained by a real snapshot transaction or by quiescing all source writers.
/// Ordinary live collection pagination is not a snapshot. Every rebuild starts a fresh generation; absent rows are
/// removed by activation. No transient notification or uncommitted checkpoint is used as correctness evidence.
/// Generations are retained until account deletion, allowing old readers to finish without mixing revisions.
/// </summary>
public sealed class LocalReadModel
{
    private readonly IOrchestrationStore _store;
    private readonly string _prefix;
    private readonly TimeProvider _time;
    private readonly IAccountResourceLock? _generationLocks;
    private readonly AccountScope? _account;
    private readonly ReadModelRetentionOptions? _retention;
    private int FormatVersion => _generationLocks is null ? 1 : 2;

    /// <summary>Creates an account-scoped projection. Use a separate model name for a separately authorized dataset.</summary>
    public LocalReadModel(IOrchestrationStore store, string name, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ValidateLabel(name, nameof(name));
        _prefix = "read-model/" + Hash(name) + "/";
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a reader-owned model in a separate v2 namespace. All participants must use the same physical
    /// generation lock provider, outside deletable data. Dispose every snapshot. Legacy v1 generations are
    /// never collected or silently migrated; rebuild this model from a stable source snapshot instead.
    /// The enclosing account lifetime must also remain owned until this model's snapshots are closed.
    /// </summary>
    public LocalReadModel(IOrchestrationStore store, string name, AccountScope account, IAccountResourceLock generationLocks,
        TimeProvider? timeProvider = null, ReadModelRetentionOptions? retention = null) : this(store, name, timeProvider)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _generationLocks = generationLocks ?? throw new ArgumentNullException(nameof(generationLocks));
        _prefix = "read-model-v2/" + account.StorageKey + "/" + Hash(name) + "/";
        _retention = retention ?? new();
        if (_retention.MaximumGenerations < 1 || _retention.MaximumRows < 1 || _retention.MaximumPayloadBytes < 1) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    private AccountScope GenerationScope(string generation) => new("Bsync.ReadModel.Generation", _account!.StorageKey, _prefix + generation);

    /// <summary>Counts stored generations and serialized UTF-16 row bytes in bounded pages under a cooperative deadline; database/index overhead is excluded.</summary>
    public async Task<ReadModelStorageStatus> GetStorageStatusAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        using var deadline = new CooperativeDeadline(budget, _time, cancellationToken, "read_model_inventory");
        var result = await StorageStatus(deadline.Token).ConfigureAwait(false); _ = deadline.Remaining; return result;
    }
    private async Task<ReadModelStorageStatus> StorageStatus(CancellationToken token)
    {
        long rows = 0, bytes = 0; var generations = 0; string? after = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var page = await _store.ReadPageAsync(_prefix, after, 128, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var record in page)
            {
                if (record.Key.StartsWith(_prefix + "generations/", StringComparison.Ordinal)) generations++;
                else if (record.Key.Length == _prefix.Length + 97 && record.Key[_prefix.Length + 32] == '/' &&
                    Guid.TryParseExact(record.Key.AsSpan(_prefix.Length, 32), "N", out _))
                { rows++; bytes = checked(bytes + 2L * record.Payload.Length); }
            }
            if (page.Count < 128) return new(generations, rows, bytes);
            after = page[^1].Key;
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(value)));
    private static void ValidateLabel(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > 256) throw new ArgumentException("Identity/version exceeds 256 characters.", parameter);
        _ = new UTF8Encoding(false, true).GetByteCount(value);
    }
    private static string Encode(ReadModelStatus value) => JsonSerializer.Serialize(value, ReadModelJson.Default.ReadModelStatus);
    private ReadModelStatus Decode(OrchestrationRecord? record)
    {
        if (record is null) return new(FormatVersion, null, null, 0);
        var value = JsonSerializer.Deserialize(record.Payload, ReadModelJson.Default.ReadModelStatus)
            ?? throw new InvalidDataException("Missing read-model metadata.");
        if (value.FormatVersion != FormatVersion) throw new LocalStoreUnavailableException("outdated", "Unsupported read-model format; data was retained.");
        if (value.AppliedRows < 0) throw new InvalidDataException("Invalid read-model progress.");
        return value;
    }

    /// <summary>Reports the last complete generation and any unfinished build. No generation is asserted to be current.</summary>
    public async Task<ReadModelStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Decode(await _store.ReadAsync(_prefix + "state", cancellationToken).ConfigureAwait(false));

    /// <summary>Opens the last complete generation without networking; returns null before first activation.</summary>
    public async Task<ReadModelSnapshot?> OpenActiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var active = (await GetStatusAsync(cancellationToken).ConfigureAwait(false)).Active;
            if (active is null) return null;
            if (_generationLocks is null) return new ReadModelSnapshot(_store, _prefix + active.Generation + "/", active);
            var access = await _generationLocks.TryAcquireAsync(GenerationScope(active.Generation), false, cancellationToken).ConfigureAwait(false);
            if (access is null) throw new AccountResourcesInUseException();
            try
            {
                // Collection may have run between observing the pointer and acquiring the shared lifetime.
                if ((await GetStatusAsync(cancellationToken).ConfigureAwait(false)).Active?.Generation == active.Generation)
                    return new ReadModelSnapshot(_store, _prefix + active.Generation + "/", active, access);
            }
            catch { await access.DisposeAsync().ConfigureAwait(false); throw; }
            await access.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes at most rowLimit rows from at most generationLimit retired v2 generations. Live shared holders,
    /// active generations remain intact. Acquiring the build fence retires abandoned incomplete staging. Process/context death releases physical ownership;
    /// elapsed time is never proof of reader death. Pass Continuation to scan later generations, then restart at null.
    /// No legacy v1 data is touched. Requires the reader-owned constructor.
    /// </summary>
    public async Task<ReadModelCleanupResult> CollectRetiredAsync(string? continuation = null, int generationLimit = 32,
        int rowLimit = 128, CancellationToken cancellationToken = default)
    {
        if (_generationLocks is null) throw new InvalidOperationException("Legacy readers have no reclaimable lifetime; use the reader-owned model constructor.");
        var indexPrefix = _prefix + "generations/";
        OrchestrationValidation.Page(indexPrefix, continuation, generationLimit);
        if (continuation is not null && !continuation.StartsWith(indexPrefix, StringComparison.Ordinal)) throw new ArgumentException("Foreign cleanup continuation.", nameof(continuation));
        ArgumentOutOfRangeException.ThrowIfLessThan(rowLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowLimit, 999);
        await using var lease = await ExecutionLease.TryAcquireAsync(_store, _prefix + "owner", TimeSpan.FromSeconds(30), _time, cancellationToken).ConfigureAwait(false);
        if (lease is null) return new(0, 0, 0, continuation, true);
        var stateRecord = await _store.ReadAsync(_prefix + "state", cancellationToken).ConfigureAwait(false);
        var state = Decode(stateRecord);
        if (state.BuildingGeneration is not null)
        {
            // Rebuild never resumes a source cursor. Owning its fence proves no prior builder can publish more
            // rows, so retaining its staging indefinitely would prevent recovery at the retention ceiling.
            state = state with { BuildingGeneration = null, AppliedRows = 0 };
            if (!await _store.TryWriteAsync([new(_prefix + "state", stateRecord!.Revision, Encode(state))], lease.Fence, cancellationToken).ConfigureAwait(false))
                return new(0, 0, 0, continuation, true);
        }
        var generations = await _store.ReadPageAsync(indexPrefix, continuation, generationLimit, cancellationToken).ConfigureAwait(false);
        var removed = 0; var protectedCount = 0; var completed = 0;
        foreach (var marker in generations)
        {
            if (marker.Payload != "2") throw new LocalStoreUnavailableException("outdated", "Unknown generation marker; data was retained.");
            var generation = marker.Key[indexPrefix.Length..];
            if (!Guid.TryParseExact(generation, "N", out _)) throw new InvalidDataException("Invalid generation marker.");
            if (generation == state.Active?.Generation || generation == state.BuildingGeneration) continue;
            await using var access = await _generationLocks.TryAcquireAsync(GenerationScope(generation), true, cancellationToken).ConfigureAwait(false);
            if (access is null) { protectedCount++; continue; }
            var rows = await _store.ReadPageAsync(_prefix + generation + "/", null, rowLimit - removed, cancellationToken).ConfigureAwait(false);
            var mutations = rows.Select(r => new OrchestrationMutation(r.Key, r.Revision, null)).ToList();
            if (rows.Count < rowLimit - removed) { mutations.Add(new(marker.Key, marker.Revision, null)); completed++; }
            if (mutations.Count > 0 && !await _store.TryWriteAsync(mutations, lease.Fence, cancellationToken).ConfigureAwait(false))
                return new(removed, completed, protectedCount, continuation, true);
            removed += rows.Count;
            // Revisit this generation until its final rows and marker are removed, including after interruption.
            if (removed == rowLimit) return new(removed, completed, protectedCount, continuation, false);
            continuation = marker.Key;
        }
        return new(removed, completed, protectedCount, generations.Count == generationLimit ? generations[^1].Key : null, false);
    }

    /// <summary>
    /// Rebuilds from a host-owned stable snapshot within a cooperative budget. Returns false on competing ownership,
    /// deadline or lease loss. Cancellation/errors leave the previous active generation readable and staging progress
    /// recoverable by restarting a full reconciliation. Source enumeration may be repeated and must have no side effects.
    /// Rows and their applied count commit together; only a fully enumerated generation can activate.
    /// </summary>
    public async Task<bool> RebuildAsync(string schemaVersion, string sourceVersion, IAsyncEnumerable<ReadModelRow> rows,
        TimeSpan budget, CancellationToken cancellationToken = default)
    {
        ValidateLabel(schemaVersion, nameof(schemaVersion)); ValidateLabel(sourceVersion, nameof(sourceVersion));
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        using var deadline = new CooperativeDeadline(budget, _time, cancellationToken, "read_model_rebuild");
        ExecutionLease? acquired;
        try { acquired = await ExecutionLease.TryAcquireAsync(_store, _prefix + "owner", TimeSpan.FromSeconds(30), _time, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        await using var lease = acquired;
        if (lease is null) return false;
        using var observation = deadline.ObserveLease(lease.Lost);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lease.Lost);
        using var cause = deadline.TrackToken(linked.Token, lease.Lost);
        var token = linked.Token;
        try
        {
            _ = deadline.Remaining;
            var stateKey = _prefix + "state";
            var record = await _store.ReadAsync(stateKey, token).ConfigureAwait(false);
            var stored = _retention is null ? new ReadModelStorageStatus(0, 0, 0) : await StorageStatus(token).ConfigureAwait(false);
            if (_retention is not null && stored.Generations >= _retention.MaximumGenerations) throw new ReadModelCapacityException();
            var generation = Guid.NewGuid().ToString("N");
            var status = Decode(record) with { BuildingGeneration = generation, AppliedRows = 0 };
            List<OrchestrationMutation> start = [new(stateKey, record?.Revision, Encode(status))];
            if (_generationLocks is not null) start.Add(new(_prefix + "generations/" + generation, null, "2"));
            if (!await _store.TryWriteAsync(start, lease.Fence, token).ConfigureAwait(false)) return false;
            record = (await _store.ReadAsync(stateKey, token).ConfigureAwait(false))!;
            var batch = new List<OrchestrationMutation>();
            var characters = 0;
            await foreach (var row in rows.WithCancellation(token).ConfigureAwait(false))
            {
                _ = deadline.Remaining;
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(row);
                ValidateLabel(row.Id, nameof(rows));
                ArgumentNullException.ThrowIfNull(row.Payload);
                if (row.Payload.Length > 262144) throw new ArgumentException("Derived row exceeds 262144 characters.", nameof(rows));
                var payload = JsonSerializer.Serialize(row, ReadModelJson.Default.ReadModelRow);
                // JSON escaping can expand text. Enforce the actual provider payload limit before staging it.
                if (payload.Length > 524288) throw new ArgumentException("Serialized derived row exceeds the metadata record limit.", nameof(rows));
                var key = _prefix + generation + "/" + Hash(row.Id);
                if (batch.Any(m => m.Key == key)) throw new InvalidDataException("Duplicate read-model row identity.");
                batch.Add(new(key, null, payload)); characters += payload.Length;
                if (batch.Count == 128 || characters >= 4 * 1024 * 1024)
                {
                    if (!await FlushAsync().ConfigureAwait(false)) return false;
                }
            }
            if (batch.Count > 0 && !await FlushAsync().ConfigureAwait(false)) return false;
            _ = deadline.Remaining;
            token.ThrowIfCancellationRequested();
            status = status with { Active = new(generation, schemaVersion, sourceVersion, status.AppliedRows, _time.GetUtcNow()), BuildingGeneration = null, AppliedRows = 0 };
            return await _store.TryWriteAsync([new(stateKey, record.Revision, Encode(status))], lease.Fence, token).ConfigureAwait(false);

            async Task<bool> FlushAsync()
            {
                _ = deadline.Remaining;
                if (_retention is not null && (batch.Count > _retention.MaximumRows - stored.Rows ||
                    2L * characters > _retention.MaximumPayloadBytes - stored.PayloadBytes)) throw new ReadModelCapacityException();
                // An existing row means a duplicate source ID. Never silently merge an incomplete snapshot.
                foreach (var mutation in batch)
                {
                    token.ThrowIfCancellationRequested();
                    if (await _store.ReadAsync(mutation.Key, token).ConfigureAwait(false) is not null)
                        throw new InvalidDataException("Duplicate read-model row identity.");
                }
                _ = deadline.Remaining;
                token.ThrowIfCancellationRequested();
                status = status with { AppliedRows = checked(status.AppliedRows + batch.Count) };
                batch.Add(new(stateKey, record.Revision, Encode(status)));
                if (!await _store.TryWriteAsync(batch, lease.Fence, token).ConfigureAwait(false)) return false;
                stored = stored with { Rows = stored.Rows + batch.Count - 1, PayloadBytes = stored.PayloadBytes + 2L * characters };
                record = (await _store.ReadAsync(stateKey, token).ConfigureAwait(false))!;
                batch.Clear(); characters = 0;
                return true;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
}

/// <summary>An immutable generation reader. Retained generations remain readable until coordinated account deletion.</summary>
public sealed class ReadModelSnapshot : IAsyncDisposable
{
    private readonly IOrchestrationStore _store;
    private readonly string _prefix;
    private IAsyncDisposable? _access;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    internal ReadModelSnapshot(IOrchestrationStore store, string prefix, ReadModelGeneration generation, IAsyncDisposable? access = null)
    { _store = store; _prefix = prefix; Generation = generation; _access = access; }

    /// <summary>The schema and source boundary actually represented by these rows.</summary>
    public ReadModelGeneration Generation { get; }

    /// <summary>Reads at most 1000 rows in opaque key order; continuation must originate from this generation.</summary>
    public async Task<ReadModelPage> ReadPageAsync(int limit, string? continuation = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OrchestrationValidation.Page(_prefix, continuation, limit);
        if (continuation is not null && !continuation.StartsWith(_prefix, StringComparison.Ordinal))
            throw new ArgumentException("Continuation belongs to another generation.", nameof(continuation));
        var records = await _store.ReadPageAsync(_prefix, continuation, limit, cancellationToken).ConfigureAwait(false);
        var rows = records.Select(r => JsonSerializer.Deserialize(r.Payload, ReadModelJson.Default.ReadModelRow)
            ?? throw new InvalidDataException("Missing derived row.")).ToArray();
        return new(rows, records.Count == limit ? records[^1].Key : null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Joins an active page read before releasing generation ownership. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (_access is { } access) await access.DisposeAsync().ConfigureAwait(false);
            _access = null; _disposed = true;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Bounded collection progress. A non-null continuation resumes the scan; protected readers may be revisited later.</summary>
/// <param name="RemovedRows">Rows removed in this call.</param>
/// <param name="RemovedGenerations">Generation markers removed.</param>
/// <param name="ProtectedGenerations">Retired generations held by live readers.</param>
/// <param name="Continuation">Next scan position, or null at the end.</param>
/// <param name="Busy">Another writer owns the model or the execution fence was lost.</param>
public sealed record ReadModelCleanupResult(int RemovedRows, int RemovedGenerations, int ProtectedGenerations, string? Continuation, bool Busy);

/// <summary>Reader-owned retention ceilings. Rebuild refuses additional staging when protected generations exhaust them.</summary>
public sealed class ReadModelRetentionOptions
{
    /// <summary>Maximum active/building/retired generation markers, default 64.</summary>
    public int MaximumGenerations { get; init; } = 64;
    /// <summary>Maximum stored rows across all generations, default one million.</summary>
    public long MaximumRows { get; init; } = 1_000_000;
    /// <summary>Maximum serialized UTF-16 row payload bytes, default 128 MiB; provider/index/encryption overhead is additional.</summary>
    public long MaximumPayloadBytes { get; init; } = 128L * 1024 * 1024;
}
/// <summary>Retained readers/data exhaust the configured read-model staging budget. Release readers and collect retired generations before retrying.</summary>
public sealed class ReadModelCapacityException() : IOException("Read-model retention budget exhausted; active data was retained.");
/// <summary>Stored row accounting; observations are not a transactional snapshot when other model instances are writing.</summary>
/// <param name="Generations">Reader-owned generation markers, including incomplete generations.</param>
/// <param name="Rows">Stored rows across all generations.</param>
/// <param name="PayloadBytes">Serialized UTF-16 row bytes, excluding provider overhead.</param>
public sealed record ReadModelStorageStatus(int Generations, long Rows, long PayloadBytes);

[JsonSerializable(typeof(ReadModelStatus))]
[JsonSerializable(typeof(ReadModelRow))]
internal partial class ReadModelJson : JsonSerializerContext;
