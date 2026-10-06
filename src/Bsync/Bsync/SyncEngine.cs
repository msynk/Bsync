using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Bsync.Clocks;
using Bsync.Conflicts;
using Bsync.Diagnostics;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Storage;
using Bsync.Transport;

namespace Bsync;

/// <summary>
/// Orchestrates replication of a single collection between a local store and a server transport.
/// </summary>
/// <remarks>
/// <para>
/// Local writes (<see cref="WriteAsync"/>, <see cref="DeleteAsync"/>) commit atomically to the store
/// and never wait for the network. Replication (<see cref="PullAsync"/>, <see cref="PushAsync"/>,
/// <see cref="SyncAsync"/>) is single-flight per engine: overlapping calls run one after another.
/// Every state change the engine makes is an atomic compare-and-transform in the store, so a local
/// edit made while a request is in flight is never overwritten or marked clean by that request.
/// </para>
/// <para>
/// Push operations carry a persisted operation id and immutable payload. When a response is lost the
/// same operation is resent, and a conforming server replays its original outcome instead of applying
/// the write again.
/// </para>
/// <para>
/// Only one engine may replicate a given store at a time; the single-flight guarantee does not extend
/// across engine instances or processes.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class SyncEngine<TDocument>
    where TDocument : class, ISyncEntity
{
    private readonly ILocalStore<TDocument> _store;
    private readonly ISyncTransport<TDocument> _transport;
    private readonly IConflictHandler<TDocument> _conflictHandler;
    private readonly HybridLogicalClock _clock;
    private readonly SyncOptions<TDocument> _options;
    private readonly Func<TDocument, TDocument> _clone;
    private readonly SemaphoreSlim _replicationGate = new(1, 1);
    private readonly object _observerGate = new();
    private ImmutableObserverList _observers = ImmutableObserverList.Empty;
    private readonly object _initGate = new();
    private readonly SyncDiagnostics.QueueState _queue;
    private readonly KeyValuePair<string, object?> _nameTag;
    private Task? _initialization;

    // What the server advertised on the last pull page (null: not known yet in this engine instance).
    private volatile IReadOnlyList<string>? _serverFeatures;
    private volatile SyncLimits? _serverLimits;
    private long _compactedThrough;

    /// <summary>
    /// Creates an engine for one collection that clones documents with reflection-based JSON. Not
    /// trim/AOT safe; use the overload that takes a cloner in trimmed or AOT-compiled apps.
    /// </summary>
    /// <param name="store">The local persistence layer.</param>
    /// <param name="transport">The client-side view of the server.</param>
    /// <param name="clock">
    /// The Hybrid Logical Clock used to stamp local writes. Before its first write the engine advances
    /// it past the store's high-water mark, so timestamps are not reused after a restart.
    /// </param>
    /// <param name="conflictHandler">
    /// The conflict strategy. Defaults to <see cref="DeferConflictHandler{TDocument}"/>: keep conflicts for resolution.
    /// </param>
    /// <param name="options">Optional tuning; sensible defaults are used when omitted.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    [RequiresUnreferencedCode("Clones documents with reflection-based JSON. Use the constructor that takes a cloner for trimmed or AOT targets.")]
    [RequiresDynamicCode("Clones documents with reflection-based JSON. Use the constructor that takes a cloner for trimmed or AOT targets.")]
    public SyncEngine(
        ILocalStore<TDocument> store,
        ISyncTransport<TDocument> transport,
        HybridLogicalClock clock,
        IConflictHandler<TDocument>? conflictHandler = null,
        SyncOptions<TDocument>? options = null)
        : this(store, transport, clock, static doc => DocumentCloner.JsonClone(doc), conflictHandler, options)
    {
    }

    /// <summary>Creates an engine for one collection with an explicit, trim/AOT-safe cloner.</summary>
    /// <param name="store">The local persistence layer.</param>
    /// <param name="transport">The client-side view of the server.</param>
    /// <param name="clock">The Hybrid Logical Clock used to stamp local writes.</param>
    /// <param name="cloner">
    /// Returns a deep, independent copy of a document, for example <c>doc =&gt; doc.Clone()</c> or
    /// <see cref="DocumentCloner.Json{T}"/> with source-generated metadata.
    /// </param>
    /// <param name="conflictHandler">The conflict strategy. Defaults to <see cref="DeferConflictHandler{TDocument}"/>.</param>
    /// <param name="options">Optional tuning; sensible defaults are used when omitted.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public SyncEngine(
        ILocalStore<TDocument> store,
        ISyncTransport<TDocument> transport,
        HybridLogicalClock clock,
        Func<TDocument, TDocument> cloner,
        IConflictHandler<TDocument>? conflictHandler = null,
        SyncOptions<TDocument>? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cloner);

        _options = options ?? new SyncOptions<TDocument>();
        _options.Validate();
        _store = store;
        _transport = transport;
        _clock = clock;
        _conflictHandler = conflictHandler ?? new DeferConflictHandler<TDocument>();
        _clone = cloner;
        _nameTag = new KeyValuePair<string, object?>(SyncDiagnostics.NameTag, _options.DiagnosticsName);
        _queue = SyncDiagnostics.Track(this, _options.DiagnosticsName);
    }

    /// <summary>Returns the app-visible documents in the local store.</summary>
    public Task<IReadOnlyList<TDocument>> QueryAsync(bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        _store.QueryAsync(includeDeleted, cancellationToken);

    /// <summary>Returns up to <paramref name="limit"/> app-visible documents after <paramref name="afterId"/>, in ordinal id order (bounded paging).</summary>
    public Task<IReadOnlyList<TDocument>> QueryPageAsync(string? afterId, int limit, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        _store.QueryPageAsync(afterId, limit, includeDeleted, cancellationToken);

    /// <summary>Returns the stored record (including sync metadata) for <paramref name="id"/>.</summary>
    public Task<SyncRecord<TDocument>?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        _store.GetAsync(id, cancellationToken);

    /// <summary>
    /// Registers <paramref name="onChange"/> to be called after every committed store transaction made by
    /// this engine that changed at least one record: one call per transaction, listing the changed ids.
    /// Dispose the result to stop observing.
    /// </summary>
    /// <remarks>
    /// Callbacks run synchronously on the thread that committed, after the commit, and must be quick;
    /// UI code should dispatch to its own context. An exception from a callback is passed to
    /// <paramref name="onError"/> (or ignored) and never affects replication. Changes made by other store
    /// instances or processes are not observed.
    /// </remarks>
    public IDisposable Observe(Action<SyncChange> onChange, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        var observer = new Observer(onChange, onError);
        lock (_observerGate)
        {
            _observers = _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    /// <summary>Returns the number of documents with local changes not yet confirmed by the server.</summary>
    public Task<int> CountDirtyAsync(CancellationToken cancellationToken = default) =>
        _store.CountDirtyAsync(cancellationToken);

    /// <summary>
    /// One-shot headless sync (task C4), for background tasks with a time limit: runs <see cref="SyncAsync"/> until no work
    /// remains, <paramref name="maxRuns"/> runs were made, or <paramref name="timeBudget"/> ran out. Running out of time is
    /// not an error: the result reports <see cref="SyncResult.HasRemainingWork"/>, every applied page and acknowledged
    /// operation is already committed with its checkpoint, and the next call resumes from there.
    /// </summary>
    public async Task<SyncResult> SyncForAsync(TimeSpan timeBudget, int maxRuns = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRuns, 1);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeBudget);
        var total = default(SyncResult);
        for (var run = 0; run < maxRuns; run++)
        {
            SyncResult result;
            try
            {
                result = await SyncAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return total with { HasRemainingWork = true };
            }

            total += result;
            if (!result.HasRemainingWork)
            {
                return total with { HasRemainingWork = false };
            }
        }

        return total with { HasRemainingWork = true };
    }

    /// <summary>Counts records that need a decision: kept conflicts and parked rejections (task C5).</summary>
    public async Task<SyncIssueCounts> CountIssuesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.CountIssuesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits a local create or update and queues it for push. A copy of <paramref name="document"/>
    /// is stored with a fresh HLC timestamp; the caller's object is not modified. The last-known server
    /// baseline is preserved so conflicts are still detected against it.
    /// </summary>
    /// <exception cref="ArgumentException">The document id is invalid.</exception>
    /// <exception cref="SyncReadOnlyException">The replica is <see cref="SyncMode.PullOnly"/>.</exception>
    public async Task<LocalWriteReceipt> WriteAsync(TDocument document, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(document);
        SyncIds.Validate(document.Id, nameof(document));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var copy = _clone(document);
        copy.UpdatedAt = _clock.Now();

        var results = await CommitAsync(SyncChangeKind.Local, 
            [new RecordUpdate<TDocument>(copy.Id, existing => existing is null
                ? new SyncRecord<TDocument>(copy, null, IsDirty: true) { LocalRevision = 1 }
                : existing with { Current = copy, IsDirty = true, LocalRevision = existing.LocalRevision + 1, Rejection = null, MissingAfterReset = false })],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new LocalWriteReceipt(copy.Id, results[0].Record!.LocalRevision, copy.UpdatedAt);
    }

    /// <summary>
    /// Soft-deletes the document with <paramref name="id"/> (stores a tombstone and queues it for push).
    /// Returns <see langword="null"/> without writing if the document is unknown locally.
    /// </summary>
    /// <exception cref="SyncReadOnlyException">The replica is <see cref="SyncMode.PullOnly"/>.</exception>
    public async Task<LocalWriteReceipt?> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        SyncIds.Validate(id);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var stamp = _clock.Now();
        var results = await CommitAsync(SyncChangeKind.Local, 
            [new RecordUpdate<TDocument>(id, existing =>
            {
                if (existing is null)
                {
                    return null;
                }

                var tombstone = _clone(existing.Current); // transforms are pure: never mutate the stored record
                tombstone.Deleted = true;
                tombstone.UpdatedAt = stamp;
                return existing with { Current = tombstone, IsDirty = true, LocalRevision = existing.LocalRevision + 1, Rejection = null, MissingAfterReset = false };
            })],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return results[0] is { Changed: true, Record: { } record }
            ? new LocalWriteReceipt(id, record.LocalRevision, stamp)
            : null;
    }

    /// <summary>
    /// Commits several local writes as one <b>dependency group</b>: they are stored atomically, uploaded in one request,
    /// and the server applies them all or none (protocol §4.1). Use it for changes that are only meaningful together,
    /// such as an order and its lines. Pass documents with <see cref="ISyncEntity.Deleted"/> set to delete them in the group.
    /// </summary>
    /// <remarks>
    /// <para>If one change of the group conflicts or is rejected, nothing of the group is applied. A conflict resolved by
    /// the conflict handler is resent with the rest of the group; a conflict kept for the user, a conflict settled with the
    /// server state, or a rejection parks the other changes with <see cref="PushErrorCodes.GroupFailed"/>. Resolving
    /// (<see cref="ResolveConflictAsync(string, TDocument, CancellationToken)"/>) or retrying (<see cref="RetryRejectedAsync"/>)
    /// any member releases the parked ones.</para>
    /// <para>Groups are sent only to servers that advertise <see cref="SyncFeatures.Groups"/>; with other servers the changes
    /// are parked with <see cref="PushErrorCodes.GroupsUnsupported"/> instead of being applied one by one. A group is sent in
    /// one request, so keep it within the server's operation limit.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The list is empty, or an id is invalid or repeated.</exception>
    public async Task<IReadOnlyList<LocalWriteReceipt>> WriteGroupAsync(IReadOnlyList<TDocument> documents, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            throw new ArgumentException("A group needs at least one document.", nameof(documents));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            ArgumentNullException.ThrowIfNull(document, nameof(documents));
            SyncIds.Validate(document.Id, nameof(documents));
            if (!ids.Add(document.Id))
            {
                throw new ArgumentException($"The document '{document.Id}' appears twice in the group.", nameof(documents));
            }
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var group = new SyncGroup(Guid.CreateVersion7().ToString("N"), [.. documents.Select(static d => d.Id)]);
        var copies = documents.Select(document =>
        {
            var copy = _clone(document);
            copy.UpdatedAt = _clock.Now();
            return copy;
        }).ToList();
        var results = await CommitAsync(
            SyncChangeKind.Local,
            [.. copies.Select(copy => new RecordUpdate<TDocument>(copy.Id, existing => existing is null
                ? new SyncRecord<TDocument>(copy, null, IsDirty: true) { LocalRevision = 1, Group = group }
                : existing with { Current = copy, IsDirty = true, LocalRevision = existing.LocalRevision + 1, Rejection = null, MissingAfterReset = false, Group = group }))],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. results.Select((result, i) => new LocalWriteReceipt(copies[i].Id, result.Record!.LocalRevision, copies[i].UpdatedAt))];
    }

    /// <summary>Returns up to <paramref name="limit"/> records with an unresolved conflict.</summary>
    public Task<IReadOnlyList<SyncRecord<TDocument>>> GetConflictsAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        _store.GetConflictsAsync(limit, cancellationToken);

    /// <summary>
    /// Resolves a kept conflict with <paramref name="resolved"/>, which becomes a new local edit based on the latest
    /// server state the replica knows. Returns <see langword="null"/> if the document has no unresolved conflict.
    /// </summary>
    public async Task<LocalWriteReceipt?> ResolveConflictAsync(string id, TDocument resolved, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        SyncIds.Validate(id);
        ArgumentNullException.ThrowIfNull(resolved);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var copy = _clone(resolved);
        copy.Id = id;
        copy.UpdatedAt = _clock.Now();
        var results = await CommitAsync(
            SyncChangeKind.Local,
            [new RecordUpdate<TDocument>(id, existing => existing?.Conflict is null
                ? null
                : existing with { Current = copy, IsDirty = true, LocalRevision = existing.LocalRevision + 1, Rejection = null, Conflict = null, MissingAfterReset = false })],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (results[0] is not { Changed: true, Record: { } record })
        {
            return null;
        }

        await ReleaseGroupAsync(record.Group, cancellationToken).ConfigureAwait(false);
        return new LocalWriteReceipt(id, record.LocalRevision, copy.UpdatedAt);
    }

    /// <summary>Discards a kept conflict: the local change is dropped and the server state stays. Returns whether one existed.</summary>
    public async Task<bool> DiscardConflictAsync(string id, CancellationToken cancellationToken = default)
    {
        SyncIds.Validate(id);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var results = await CommitAsync(
            SyncChangeKind.Local,
            // The record shows the latest known server state again (it may have shown the local edit, D7).
            [new RecordUpdate<TDocument>(id, existing => existing?.Conflict is null ? null : existing with { Conflict = null, Current = existing.Base is { } server ? _clone(server) : existing.Current })],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return results[0].Changed;
    }

    /// <summary>Returns up to <paramref name="limit"/> records whose latest local change the server rejected.</summary>
    public Task<IReadOnlyList<SyncRecord<TDocument>>> GetRejectedAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        _store.GetRejectedAsync(limit, cancellationToken);

    /// <summary>
    /// Sends a rejected change again, as a new operation with a fresh timestamp (for example after the server's rules
    /// or the device clock were fixed). Returns <see langword="null"/> if the record is not rejected.
    /// </summary>
    public async Task<LocalWriteReceipt?> RetryRejectedAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        SyncIds.Validate(id);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var stamp = _clock.Now();
        var results = await CommitAsync(
            SyncChangeKind.Local,
            [new RecordUpdate<TDocument>(id, existing =>
            {
                if (existing?.Rejection is null)
                {
                    return null;
                }

                var copy = _clone(existing.Current);
                copy.UpdatedAt = stamp;
                return existing with { Current = copy, IsDirty = true, LocalRevision = existing.LocalRevision + 1, Pending = null, Rejection = null };
            })],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (results[0] is not { Changed: true, Record: { } record })
        {
            return null;
        }

        await ReleaseGroupAsync(record.Group, cancellationToken).ConfigureAwait(false);
        return new LocalWriteReceipt(id, record.LocalRevision, stamp);
    }

    /// <summary>Clears the <see cref="PushErrorCodes.GroupFailed"/> and <see cref="PushErrorCodes.GroupsUnsupported"/> parking of a group's other changes.</summary>
    private async Task ReleaseGroupAsync(SyncGroup? group, CancellationToken cancellationToken)
    {
        if (group is null)
        {
            return;
        }

        await CommitAsync(
            SyncChangeKind.Local,
            [.. group.Members.Select(member => new RecordUpdate<TDocument>(member, existing =>
                existing is { Group: { } g, Rejection.ErrorCode: PushErrorCodes.GroupFailed or PushErrorCodes.GroupsUnsupported } && g.Id == group.Id
                    ? existing with { Rejection = null, Pending = null }
                    : null))],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parks the pushable changes of <paramref name="group"/> (a member failed, or groups are unsupported).</summary>
    private async Task ParkGroupAsync(SyncGroup group, string code, string message, CancellationToken cancellationToken)
    {
        await CommitAsync(
            SyncChangeKind.Sync,
            [.. group.Members.Select(member => new RecordUpdate<TDocument>(member, existing =>
                existing is { IsDirty: true, Rejection: null, Group: { } g } && g.Id == group.Id
                    ? existing with { Rejection = new SyncRejection(existing.LocalRevision, code, message) }
                    : null))],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards the unsynchronized local change of one document (pending or rejected) and returns it to the newest
    /// server state the replica knows. A document the server has never confirmed is hidden like a missing record
    /// until a pull brings it. Returns whether there was a change to discard.
    /// </summary>
    /// <remarks>
    /// An operation already sent cannot be recalled: if the server accepted it before the revert, the next pull shows
    /// that state. A kept conflict is not affected; see <see cref="DiscardConflictAsync"/>.
    /// </remarks>
    public async Task<bool> RevertAsync(string id, CancellationToken cancellationToken = default)
    {
        SyncIds.Validate(id);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var results = await CommitAsync(
            SyncChangeKind.Local,
            [new RecordUpdate<TDocument>(id, existing =>
            {
                if (existing is not { IsDirty: true })
                {
                    return null;
                }

                var clean = existing with { IsDirty = false, LocalRevision = existing.LocalRevision + 1, Pending = null, Rejection = null, Observed = null, ObservedVersion = null };
                if (existing.ObservedVersion is { } observed && existing.Observed is { } newer && (existing.BaseVersion is not { } baseVersion || observed > baseVersion))
                {
                    return clean with { Current = _clone(newer), Base = _clone(newer), BaseVersion = observed, MissingAfterReset = false };
                }

                return existing.Base is { } confirmed
                    ? clean with { Current = _clone(confirmed), MissingAfterReset = false }
                    : clean with { MissingAfterReset = true };
            })],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return results[0].Changed;
    }

    /// <summary>
    /// Returns every record that holds local work: unsynchronized changes (pending or rejected) and kept conflicts,
    /// with their pending operations. Use it to move work to a rebuilt or new store with
    /// <see cref="ImportLocalChangesAsync"/>, or to show it to support. Not bounded: it reads all such records.
    /// </summary>
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> ExportLocalChangesAsync(CancellationToken cancellationToken = default)
    {
        var byId = new Dictionary<string, SyncRecord<TDocument>>(StringComparer.Ordinal);
        foreach (var record in (await _store.GetPendingAsync(int.MaxValue, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Concat(await _store.GetRejectedAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
            .Concat(await _store.GetConflictsAsync(int.MaxValue, cancellationToken).ConfigureAwait(false)))
        {
            byId[record.Current.Id] = record;
        }

        return [.. byId.Values.OrderBy(static r => r.Current.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Adds exported local work to this replica. A record is imported only where this replica has no local work of
    /// its own for the id (absent, or clean without a conflict); pending operations keep their ids, so a resend is
    /// replayed by the server rather than applied twice. Returns the ids that were skipped.
    /// </summary>
    public async Task<IReadOnlyList<string>> ImportLocalChangesAsync(IReadOnlyList<SyncRecord<TDocument>> records, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(records);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var generation = (await _store.GetCursorAsync(cancellationToken).ConfigureAwait(false)).Generation;
        var skipped = new List<string>();
        foreach (var batch in records.Chunk(_options.PushBatchSize))
        {
            var results = await CommitAsync(
                SyncChangeKind.Local,
                [.. batch.Select(record => new RecordUpdate<TDocument>(record.Current.Id, existing =>
                    existing is null or { IsDirty: false, Conflict: null }
                        ? CloneRecord(record) with { Generation = Math.Min(record.Generation, generation), MissingAfterReset = false }
                        : null))],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            skipped.AddRange(results.Select((r, i) => (r.Changed, batch[i].Current.Id)).Where(static r => !r.Changed).Select(static r => r.Id));
        }

        foreach (var record in records)
        {
            _clock.Update(record.Current.UpdatedAt);
            if (record.Pending is { } pending)
            {
                _clock.Update(pending.Payload.UpdatedAt);
            }
        }

        return skipped;
    }

    private SyncRecord<TDocument> CloneRecord(SyncRecord<TDocument> record) => record with
    {
        Current = _clone(record.Current),
        Base = record.Base is { } confirmed ? _clone(confirmed) : null,
        Observed = record.Observed is { } observed ? _clone(observed) : null,
        Pending = record.Pending is { } pending ? pending with { Payload = _clone(pending.Payload) } : null,
        Conflict = record.Conflict is { } conflict
            ? conflict with { Server = _clone(conflict.Server), Local = _clone(conflict.Local), Base = conflict.Base is { } ancestor ? _clone(ancestor) : null }
            : null,
    };

    /// <summary>Runs a full sync: pull server changes, then push local writes.</summary>
    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _replicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync("sync", async () =>
            {
                var pull = await PullCoreAsync(cancellationToken).ConfigureAwait(false);
                var push = await PushCoreAsync(cancellationToken).ConfigureAwait(false);
                return pull + push;
            }).ConfigureAwait(false);
        }
        finally
        {
            _replicationGate.Release();
        }
    }

    /// <summary>
    /// Pulls change-feed pages after the stored checkpoint and applies each page, together with its
    /// checkpoint, in one atomic store update. Records with unconfirmed local changes are left untouched
    /// so their divergence is resolved during push.
    /// </summary>
    /// <remarks>
    /// When the server can no longer serve the stored checkpoint (<see cref="SyncResetRequiredException"/>),
    /// the engine starts a new generation and pulls a full snapshot. Pending local changes and their
    /// operations are kept. Clean records the snapshot does not contain are marked
    /// <see cref="SyncRecord{TDocument}.MissingAfterReset"/> and hidden from queries; they are not deleted
    /// on the server.
    /// </remarks>
    public async Task<SyncResult> PullAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _replicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync("pull", () => PullCoreAsync(cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _replicationGate.Release();
        }
    }

    /// <summary>
    /// Pushes pending local changes in batches until the queue is drained or a work budget is reached.
    /// Accepted operations adopt the server's authoritative state unless the record was edited again in
    /// the meantime; conflicts are resolved by the configured <see cref="IConflictHandler{TDocument}"/>.
    /// </summary>
    public async Task<SyncResult> PushAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _replicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync("push", () => PushCoreAsync(cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _replicationGate.Release();
        }
    }

    /// <summary>Runs one replication operation inside a trace span, and records its duration and the queue afterwards.</summary>
    private async Task<SyncResult> RunAsync(string operation, Func<Task<SyncResult>> run)
    {
        using var activity = SyncDiagnostics.ActivitySource.StartActivity($"bsync.{operation}");
        activity?.SetTag(SyncDiagnostics.NameTag, _options.DiagnosticsName);
        var started = Stopwatch.GetTimestamp();
        var result = "error";
        string? errorType = null;
        try
        {
            var outcome = await run().ConfigureAwait(false);
            result = outcome.IsComplete ? "complete" : "incomplete";
            if (activity is not null)
            {
                activity.SetTag("bsync.pulled", outcome.Pulled);
                activity.SetTag("bsync.pushed", outcome.Pushed);
                activity.SetTag("bsync.conflicts", outcome.Conflicts);
                activity.SetTag("bsync.rejected", outcome.Rejected);
                activity.SetTag("bsync.deferred", outcome.Deferred);
                activity.SetTag("bsync.reset", outcome.ResetPerformed);
                activity.SetTag("bsync.result", result);
            }

            return outcome;
        }
        catch (Exception error)
        {
            errorType = error switch
            {
                SyncTransportException transport => transport.ErrorCode,
                OperationCanceledException => "cancelled",
                _ => error.GetType().Name,
            };
            activity?.SetStatus(ActivityStatusCode.Error, errorType);
            activity?.SetTag("error.type", errorType);
            throw;
        }
        finally
        {
            if (SyncDiagnostics.RunDuration.Enabled)
            {
                var tags = new TagList { _nameTag, { "bsync.operation", operation }, { "bsync.result", result } };
                if (errorType is not null)
                {
                    tags.Add("error.type", errorType);
                }

                SyncDiagnostics.RunDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            }

            await MeasureQueueAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Refreshes the queue gauges, only while someone listens to them.</summary>
    private async Task MeasureQueueAsync()
    {
        if (!SyncDiagnostics.QueueDepth.Enabled && !SyncDiagnostics.QueueOldestAge.Enabled && !SyncDiagnostics.Issues.Enabled)
        {
            return;
        }

        try
        {
            if (SyncDiagnostics.Issues.Enabled)
            {
                _queue.Issues = await _store.CountIssuesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var depth = await _store.CountDirtyAsync(CancellationToken.None).ConfigureAwait(false);
            var oldest = await _store.GetPendingAsync(1, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            _queue.Update(depth, oldest.Count == 0 ? null : oldest[0].Current.UpdatedAt.WallTime);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Diagnostics must never change the outcome of a run (the store may be closing, for example).
        }
    }

    private static string OutcomeName(PushOutcomeKind kind) => kind switch
    {
        PushOutcomeKind.Accepted => "accepted",
        PushOutcomeKind.Conflict => "conflict",
        PushOutcomeKind.Rejected => "rejected",
        PushOutcomeKind.RetryLater => "retry-later",
        _ => "unknown",
    };

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        lock (_initGate)
        {
            // Shared by all callers, so it must not observe any one caller's cancellation.
            if (_initialization is null || _initialization.IsFaulted)
            {
                _initialization = InitializeAsync();
            }

            return _initialization.WaitAsync(cancellationToken);
        }
    }

    private async Task InitializeAsync()
    {
        var highWater = await _store.GetClockHighWaterAsync(CancellationToken.None).ConfigureAwait(false);
        if (highWater != HlcTimestamp.MinValue)
        {
            _clock.Update(highWater);
        }
    }

    private async Task<SyncResult> PullCoreAsync(CancellationToken cancellationToken)
    {
        var applied = 0;
        var removed = 0;
        var compacted = 0;
        var reset = false;
        var cursor = await _store.GetCursorAsync(cancellationToken).ConfigureAwait(false);

        for (var page = 0; page < _options.MaxPullPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PullResult<TDocument> result;
            var sent = _clock.PhysicalMilliseconds();
            try
            {
                result = await _transport
                    .PullAsync(new PullRequest(cursor.Checkpoint, PullBatchSize) { Features = ReplicaFeatures, Replica = _clock.Node }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SyncResetRequiredException required) when (!cursor.Checkpoint.IsStart)
            {
                // Start a new generation and a full snapshot, atomically. Local records are untouched. After a change
                // of access or retention, records the snapshot lacks are removed rather than kept hidden.
                cursor = new ReplicaCursor(Checkpoint.Start, cursor.Generation + 1, Resnapshot: true, PurgeMissing: required.Reason is ResetReasons.ScopeChanged or ResetReasons.Expired);
                await CommitAsync(SyncChangeKind.Remote, [], cursor, cancellationToken).ConfigureAwait(false);
                reset = true;
                SyncDiagnostics.Resets.Add(1, _nameTag, new KeyValuePair<string, object?>("bsync.reason", required.Reason));
                Activity.Current?.AddEvent(new ActivityEvent("bsync.reset", tags: new ActivityTagsCollection { ["bsync.reason"] = required.Reason }));
                continue;
            }

            ValidatePullPage(result, cursor.Checkpoint);
            _serverFeatures = result.Features ?? [];
            _serverLimits = result.Features?.Contains(SyncFeatures.Limits, StringComparer.Ordinal) == true
                && result.Limits is { MaxOperationsPerPush: > 0, MaxPageSize: > 0 } limits ? limits : null;
            if (result.Features?.Contains(SyncFeatures.ServerTime, StringComparer.Ordinal) == true && result.ServerTime is { } serverTime)
            {
                CorrectClock(serverTime, sent, _clock.PhysicalMilliseconds());
            }

            var generation = cursor.Generation;
            var updates = new List<RecordUpdate<TDocument>>(result.Changes.Count);
            var removals = result.Features?.Contains(SyncFeatures.Removals, StringComparer.Ordinal) == true ? result.Removals ?? [] : [];
            if (removals.Count > 0)
            {
                // ADR-015: clean copies leave the device. The purge runs before the page commits, so an interrupted run
                // repeats it (it is idempotent); records with local changes or a kept conflict are hidden in the same
                // atomic update as the page, and their uploads are answered forbidden and parked.
                removed += await _store.PurgeAsync(removals, generation + 1, cancellationToken).ConfigureAwait(false);
                foreach (var id in removals)
                {
                    updates.Add(new RecordUpdate<TDocument>(id, existing =>
                        existing is { MissingAfterReset: false } && (existing.IsDirty || existing.Conflict is not null)
                            ? existing with { MissingAfterReset = true, Generation = generation }
                            : null));
                }

                Notify(new SyncChange(SyncChangeKind.Remote, removals));
            }

            foreach (var change in result.Changes)
            {
                // Keep the local clock ahead of every timestamp observed from other replicas.
                _clock.Update(change.Document.UpdatedAt);
                updates.Add(new RecordUpdate<TDocument>(change.Document.Id, existing => ApplyRemote(existing, change, generation)));
            }

            var next = cursor with { Checkpoint = result.Checkpoint };
            var results = await CommitAsync(SyncChangeKind.Remote, updates, next, cancellationToken).ConfigureAwait(false);

            // D4: tombstones the server has purged are kept by nobody else; drop this replica's clean copies too, so they
            // do not accumulate forever. Versions compare only within the current generation.
            if (result.Features?.Contains(SyncFeatures.Retention, StringComparer.Ordinal) == true
                && result.RetentionHorizon is { } horizon && horizon > Interlocked.Read(ref _compactedThrough) && !next.Resnapshot)
            {
                compacted += await _store.PurgeTombstonesAsync(horizon, generation, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _compactedThrough, horizon);
            }
            // The removal updates come first in the batch, then one per change.
            removed += results.Take(removals.Count).Count(static r => r.Changed);
            var changed = results.Skip(removals.Count).Count(static r => r.Changed);
            applied += changed;
            SyncDiagnostics.PulledChanges.Add(changed, _nameTag);
            cursor = next;

            if (!result.HasMore)
            {
                var swept = cursor.Resnapshot
                    ? await CompleteResnapshotAsync(cursor, cancellationToken).ConfigureAwait(false)
                    : default;
                return new SyncResult(applied, 0, 0)
                {
                    ResetPerformed = reset,
                    MissingAfterReset = swept.Hidden,
                    PurgedAfterReset = swept.Purged,
                    Removed = removed,
                    Compacted = compacted,
                };
            }
        }

        return new SyncResult(applied, 0, 0) { HasRemainingWork = true, ResetPerformed = reset, Removed = removed, Compacted = compacted };
    }

    /// <summary>
    /// Marks clean records that the completed snapshot did not contain as missing (or purges them), in bounded
    /// batches, then leaves resnapshot mode. Safe to repeat after a crash: the cursor stays in resnapshot mode
    /// until every stale record is handled.
    /// </summary>
    private async Task<(int Hidden, int Purged)> CompleteResnapshotAsync(ReplicaCursor cursor, CancellationToken cancellationToken)
    {
        var hidden = 0;
        var purged = 0;
        var generation = cursor.Generation;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stale = await _store.GetStaleAsync(generation, _options.PullBatchSize, cancellationToken).ConfigureAwait(false);
            if (stale.Count == 0)
            {
                await CommitAsync(SyncChangeKind.Remote, [], cursor with { Resnapshot = false, PurgeMissing = false }, cancellationToken).ConfigureAwait(false);
                return (hidden, purged);
            }

            if (cursor.PurgeMissing)
            {
                var ids = stale.Select(static r => r.Current.Id).ToList();
                purged += await _store.PurgeAsync(ids, generation, cancellationToken).ConfigureAwait(false);
                Notify(new SyncChange(SyncChangeKind.Remote, ids));

                // A record that keeps an unresolved conflict still holds a local change, so it is not purged; it is
                // hidden like after an epoch reset (the conflict stays listed). The loop below handles it.
            }

            var updates = stale
                .Select(record => new RecordUpdate<TDocument>(record.Current.Id, existing =>
                    existing is { IsDirty: false, MissingAfterReset: false } && existing.Generation < generation
                        ? existing with { MissingAfterReset = true, Generation = generation }
                        : null))
                .ToList();
            var results = await CommitAsync(SyncChangeKind.Remote, updates, cancellationToken: cancellationToken).ConfigureAwait(false);
            hidden += results.Count(static r => r.Changed);
        }
    }

    /// <summary>
    /// Sends a batch. When the server refuses it as too large (<c>payload-too-large</c>, nothing applied), sends each half
    /// separately, down to a single document or dependency group. One that is too large on its own is parked locally with
    /// a <see cref="PushErrorCodes.PayloadTooLarge"/> rejection, so the rest of the queue keeps moving (I19); after making
    /// it smaller, <see cref="RetryRejectedAsync"/> sends it as a new operation.
    /// </summary>
    private async Task<List<PushOutcome<TDocument>>> SendSplittingAsync(IReadOnlyList<PushOperation<TDocument>> operations, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _transport.PushAsync(new PushRequest<TDocument>(operations), cancellationToken).ConfigureAwait(false);
            return response?.Outcomes is { } outcomes ? [.. outcomes] : throw new SyncProtocolException("The server returned no push result.");
        }
        catch (SyncTransportException tooLarge) when (tooLarge.ErrorCode == SyncErrorCodes.PayloadTooLarge && !tooLarge.IsTransient)
        {
            // A group is never split: its members are applied together or not at all (protocol §4.1).
            var units = operations
                .Select((operation, index) => (operation, key: operation.Group ?? "\0" + index.ToString(CultureInfo.InvariantCulture)))
                .GroupBy(pair => pair.key, StringComparer.Ordinal)
                .Select(unit => unit.Select(pair => pair.operation).ToList())
                .ToList();
            if (units.Count == 1)
            {
                return [.. operations.Select(o => PushOutcome<TDocument>.Rejected(o.OperationId, PushErrorCodes.PayloadTooLarge, "Too large for the server, even on its own. Make it smaller, then retry it."))];
            }

            var half = units.Count / 2;
            var first = await SendSplittingAsync([.. units.Take(half).SelectMany(u => u)], cancellationToken).ConfigureAwait(false);
            var second = await SendSplittingAsync([.. units.Skip(half).SelectMany(u => u)], cancellationToken).ConfigureAwait(false);
            return [.. first, .. second];
        }
    }

    /// <summary>
    /// Task D2: estimates the server's clock at the midpoint of the request. When this device's clock is ahead (by a second
    /// or more, beyond network jitter), the local clock is corrected by the difference, so writes are stamped with times
    /// the server accepts instead of being rejected with <c>clock-skew</c>. A clock that is behind is left alone: the server
    /// accepts its writes, and origin timestamps stay the device's own.
    /// </summary>
    private void CorrectClock(long serverTime, long sent, long received)
    {
        var offset = serverTime - (sent + ((received - sent) / 2));
        _clock.PhysicalOffset = offset <= -1_000 ? TimeSpan.FromMilliseconds(offset) : TimeSpan.Zero;
    }

    /// <summary>The response features this replica understands, sent with every pull.</summary>
    private static readonly IReadOnlyList<string> ReplicaFeatures = [SyncFeatures.Removals];

    // The configured batch sizes, clamped to the limits the server advertised (feature "limits"), so a replica never
    // sends a push the server must refuse as too large.
    private int PullBatchSize => _serverLimits is { } limits ? Math.Min(_options.PullBatchSize, limits.MaxPageSize) : _options.PullBatchSize;

    private int PushBatchSize => _serverLimits is { } limits ? Math.Min(_options.PushBatchSize, limits.MaxOperationsPerPush) : _options.PushBatchSize;

    private void ValidatePullPage(PullResult<TDocument>? result, Checkpoint requested)
    {
        if (result?.Changes is null)
        {
            throw new SyncProtocolException("The server returned no pull result.");
        }

        if (result.Changes.Count > PullBatchSize)
        {
            throw new SyncProtocolException("The server returned more changes than requested.");
        }

        if (result.HasMore && result.Checkpoint == requested)
        {
            throw new SyncProtocolException("The server reported more changes without advancing the checkpoint.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in result.Changes)
        {
            if (change?.Document is null || !SyncIds.IsValid(change.Document.Id) || change.Version < 1)
            {
                throw new SyncProtocolException("The server returned a malformed change.");
            }

            if (!ids.Add(change.Document.Id))
            {
                throw new SyncProtocolException($"The server returned document '{change.Document.Id}' twice in one page.");
            }
        }

        foreach (var id in result.Removals ?? [])
        {
            if (!SyncIds.IsValid(id) || !ids.Add(id))
            {
                throw new SyncProtocolException("The server returned a malformed or repeated removal.");
            }
        }

        if (ids.Count > PullBatchSize)
        {
            throw new SyncProtocolException("The server returned more entries than requested.");
        }
    }

    private void EnsureWritable()
    {
        if (_options.Mode == SyncMode.PullOnly)
        {
            throw new SyncReadOnlyException();
        }
    }

    private SyncRecord<TDocument>? ApplyRemote(SyncRecord<TDocument>? existing, RemoteChange<TDocument> change, long generation)
    {
        // A pull-only replica never edits, so it never needs the base copy that conflict detection and merges use.
        var pullOnly = _options.Mode == SyncMode.PullOnly;
        if (existing is null)
        {
            return new SyncRecord<TDocument>(_clone(change.Document), pullOnly ? null : _clone(change.Document), IsDirty: false)
            {
                BaseVersion = change.Version,
                Generation = generation,
            };
        }

        // Never regress to an older or equal server version (duplicate or reordered delivery). Versions
        // from an earlier generation are not compared: after a restore the server may legitimately serve
        // an older state than the replica last saw.
        if (existing.Generation == generation && existing.KnownVersion is { } known && known >= change.Version)
        {
            return null;
        }

        // Unconfirmed local changes win locally until push resolves the divergence. The base stays the
        // state the edit was made against; the newer server state is remembered because the checkpoint
        // moves past it.
        if (existing.IsDirty)
        {
            // A dirty record hidden because it had left the caller's view (ADR-015) is visible again once it is back.
            return existing with { Observed = _clone(change.Document), ObservedVersion = change.Version, Generation = generation, MissingAfterReset = false };
        }

        // A kept conflict that shows the local edit keeps showing it; the newer server state becomes the base the decision
        // is made against (D7).
        var showLocal = existing.Conflict is not null && _options.KeptConflictView == KeptConflictView.Local;
        return existing with
        {
            Current = showLocal ? existing.Current : _clone(change.Document),
            Conflict = showLocal ? existing.Conflict! with { Server = _clone(change.Document), ServerVersion = change.Version } : existing.Conflict,
            Base = pullOnly ? null : _clone(change.Document),
            BaseVersion = change.Version,
            Observed = null,
            ObservedVersion = null,
            Generation = generation,
            MissingAfterReset = false,
        };
    }

    /// <summary>
    /// Drops an observed server state that the base has caught up with, and makes a clean record adopt an
    /// observed state that is newer than its base.
    /// </summary>
    private SyncRecord<TDocument> Settle(SyncRecord<TDocument> record)
    {
        if (record.ObservedVersion is not { } observed)
        {
            return record;
        }

        if (record.BaseVersion is { } baseVersion && observed <= baseVersion)
        {
            return record with { Observed = null, ObservedVersion = null };
        }

        return record.IsDirty
            ? record
            : record with
            {
                Current = _clone(record.Observed!),
                Base = _clone(record.Observed!),
                BaseVersion = observed,
                Observed = null,
                ObservedVersion = null,
            };
    }

    private async Task<SyncResult> PushCoreAsync(CancellationToken cancellationToken)
    {
        var pushed = 0;
        var conflicts = 0;
        var rejected = 0;
        var deferred = 0;
        var generation = (await _store.GetCursorAsync(cancellationToken).ConfigureAwait(false)).Generation;
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var conflictCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var batch = 0; batch < _options.MaxPushBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = await _store.GetPendingAsync(PushBatchSize, excluded, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                break;
            }

            candidates = await ExpandGroupsAsync(candidates, excluded, cancellationToken).ConfigureAwait(false);
            if (_options.ReadyToPush is { } ready && candidates.Count > 0)
            {
                var before = candidates.Count;
                candidates = await HoldBackAsync(candidates, ready, excluded, cancellationToken).ConfigureAwait(false);
                deferred += before - candidates.Count;
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            var operations = await PrepareOperationsAsync(candidates, cancellationToken).ConfigureAwait(false);
            if (operations.Count == 0)
            {
                continue;
            }

            var response = new PushResult<TDocument>(await SendSplittingAsync(operations, cancellationToken).ConfigureAwait(false));
            var outcomes = CorrelateOutcomes(operations, response);
            if (SyncDiagnostics.Operations.Enabled)
            {
                foreach (var operation in operations)
                {
                    var tags = new TagList { _nameTag };
                    if (outcomes.TryGetValue(operation.OperationId, out var outcome))
                    {
                        tags.Add("bsync.outcome", OutcomeName(outcome.Kind));
                        tags.Add("bsync.duplicate", outcome.IsDuplicate);
                        if (outcome.Kind == PushOutcomeKind.Rejected)
                        {
                            tags.Add("error.type", outcome.ErrorCode ?? "rejected");
                        }
                    }
                    else
                    {
                        tags.Add("bsync.outcome", "missing");
                    }

                    SyncDiagnostics.Operations.Add(1, tags);
                }
            }

            var acknowledgements = new List<RecordUpdate<TDocument>>();
            var failedGroups = new List<string>();
            foreach (var operation in operations)
            {
                if (!outcomes.TryGetValue(operation.OperationId, out var outcome))
                {
                    // Unknown result: keep the operation pending and resend the same id next time.
                    excluded.Add(operation.DocumentId);
                    deferred++;
                    continue;
                }

                switch (outcome.Kind)
                {
                    case PushOutcomeKind.Accepted:
                        _clock.Update(outcome.Document!.UpdatedAt);
                        acknowledgements.Add(new RecordUpdate<TDocument>(
                            operation.DocumentId,
                            existing => ApplyAccepted(existing, operation.OperationId, outcome, generation)));
                        pushed++;
                        break;

                    case PushOutcomeKind.Rejected:
                        acknowledgements.Add(new RecordUpdate<TDocument>(
                            operation.DocumentId,
                            existing => ApplyRejected(existing, operation.OperationId, outcome)));
                        excluded.Add(operation.DocumentId);
                        rejected++;
                        if (operation.Group is not null)
                        {
                            failedGroups.Add(operation.DocumentId);
                        }

                        break;

                    case PushOutcomeKind.RetryLater when outcome.ErrorCode == PushErrorCodes.GroupAborted:
                        // Not decided: the group goes again with the member that failed once that member is settled
                        // (or is parked with it, or skipped with it if that member is excluded from this run).
                        break;

                    case PushOutcomeKind.RetryLater:
                        excluded.Add(operation.DocumentId);
                        deferred++;
                        break;
                }
            }

            await CommitAsync(SyncChangeKind.Sync, acknowledgements, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var operation in operations)
            {
                if (outcomes.TryGetValue(operation.OperationId, out var outcome) && outcome.Kind == PushOutcomeKind.Conflict)
                {
                    conflicts++;
                    var count = conflictCounts[operation.DocumentId] = conflictCounts.GetValueOrDefault(operation.DocumentId) + 1;
                    var stillPending = await ResolveConflictAsync(operation.OperationId, outcome, generation, cancellationToken).ConfigureAwait(false);
                    if (stillPending && count >= _options.MaxConflictRetries)
                    {
                        excluded.Add(operation.DocumentId);
                        deferred++;
                    }

                    if (!stillPending && operation.Group is not null)
                    {
                        // Kept for the user, or settled with the server state: the group cannot be applied as a whole.
                        failedGroups.Add(operation.DocumentId);
                    }
                }
            }

            foreach (var failed in failedGroups)
            {
                if (await _store.GetAsync(failed, cancellationToken).ConfigureAwait(false) is { Group: { } group })
                {
                    await ParkGroupAsync(group, PushErrorCodes.GroupFailed, $"Another change of the same group ('{failed}') was not accepted; resolve or retry it.", cancellationToken).ConfigureAwait(false);
                    foreach (var member in group.Members)
                    {
                        excluded.Add(member);
                    }
                }
            }
        }

        var remaining = await _store.GetPendingAsync(1, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new SyncResult(0, pushed, conflicts)
        {
            Rejected = rejected,
            Deferred = deferred,
            HasRemainingWork = remaining.Count > 0,
        };
    }

    /// <summary>
    /// Removes candidates that <see cref="SyncOptions{TDocument}.ReadyToPush"/> holds back, with every member of their
    /// dependency groups, and excludes them from the rest of this run.
    /// </summary>
    private static async Task<IReadOnlyList<SyncRecord<TDocument>>> HoldBackAsync(
        IReadOnlyList<SyncRecord<TDocument>> candidates,
        Func<TDocument, CancellationToken, ValueTask<bool>> ready,
        HashSet<string> excluded,
        CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!await ready(candidate.Current, cancellationToken).ConfigureAwait(false))
            {
                held.Add(candidate.Current.Id);
                if (candidate.Group is { } group)
                {
                    held.UnionWith(group.Members);
                }
            }
        }

        if (held.Count == 0)
        {
            return candidates;
        }

        excluded.UnionWith(held);
        return candidates.Where(c => !held.Contains(c.Current.Id)).ToList();
    }

    /// <summary>
    /// Adds every pending member of each dependency group among <paramref name="candidates"/>, so a group always travels in
    /// one request. A group with a parked member is parked as a whole; groups wait for the server's features and are parked
    /// if the server does not support them.
    /// </summary>
    private async Task<IReadOnlyList<SyncRecord<TDocument>>> ExpandGroupsAsync(
        IReadOnlyList<SyncRecord<TDocument>> candidates,
        HashSet<string> excluded,
        CancellationToken cancellationToken)
    {
        if (candidates.All(static c => c.Group is null))
        {
            return candidates;
        }

        var expanded = new List<SyncRecord<TDocument>>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Current.Id))
            {
                continue;
            }

            if (candidate.Group is not { } group)
            {
                expanded.Add(candidate);
                continue;
            }

            if (_serverFeatures is not { } features)
            {
                // Not known until the first pull of this engine (SyncAsync pulls first): wait.
                excluded.Add(candidate.Current.Id);
                continue;
            }

            if (!features.Contains(SyncFeatures.Groups, StringComparer.Ordinal))
            {
                await ParkGroupAsync(group, PushErrorCodes.GroupsUnsupported, "The server does not apply dependency groups atomically.", cancellationToken).ConfigureAwait(false);
                excluded.UnionWith(group.Members);
                continue;
            }

            if (group.Members.Any(excluded.Contains))
            {
                // A member is out of this run (retry-later, conflict budget): the group waits with it.
                excluded.UnionWith(group.Members);
                seen.UnionWith(group.Members);
                continue;
            }

            var members = new List<SyncRecord<TDocument>>(group.Members.Count);
            var blocked = false;
            foreach (var id in group.Members)
            {
                var member = id == candidate.Current.Id ? candidate : await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
                if (member is { IsDirty: true, Group: { } g } && g.Id == group.Id)
                {
                    blocked |= member.Rejection is not null;
                    members.Add(member);
                }
            }

            seen.UnionWith(group.Members);
            if (blocked)
            {
                await ParkGroupAsync(group, PushErrorCodes.GroupFailed, "Another change of the same group is waiting for a decision.", cancellationToken).ConfigureAwait(false);
                excluded.UnionWith(group.Members);
                continue;
            }

            expanded.AddRange(members);
        }

        return expanded;
    }

    /// <summary>
    /// Persists a new immutable operation for each candidate that does not already have one, then returns
    /// every candidate's pending operation. Existing pending operations are resent unchanged, unless the candidate's
    /// dependency group changed since the operation was made (then a new operation replaces it: an operation that may
    /// already have been applied carries an old base version, so resending under a new id conflicts and is never applied twice).
    /// </summary>
    private async Task<List<PushOperation<TDocument>>> PrepareOperationsAsync(
        IReadOnlyList<SyncRecord<TDocument>> candidates,
        CancellationToken cancellationToken)
    {
        var groupSizes = candidates.Where(static c => c.Group is not null).GroupBy(static c => c.Group!.Id, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.Ordinal);
        var updates = new List<RecordUpdate<TDocument>>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var operationId = Guid.CreateVersion7().ToString("N");
            var group = candidate.Group?.Id;
            var size = group is null ? 0 : groupSizes[group];
            updates.Add(new RecordUpdate<TDocument>(candidate.Current.Id, existing =>
                existing is { IsPushable: true } && (existing.Pending is null || existing.Pending.Group != group || existing.Pending.GroupSize != size) && existing.Group?.Id == group
                    ? existing with
                    {
                        Pending = new PendingOperation<TDocument>(operationId, existing.LocalRevision, existing.BaseVersion, existing.Current) { Group = group, GroupSize = size },
                    }
                    : null));
        }

        var results = await CommitAsync(SyncChangeKind.Sync, updates, cancellationToken: cancellationToken).ConfigureAwait(false);
        var operations = new List<PushOperation<TDocument>>(results.Count);
        foreach (var result in results)
        {
            if (result.Record is { IsPushable: true, Pending: { } pending } record)
            {
                operations.Add(new PushOperation<TDocument>(pending.OperationId, record.Current.Id, pending.BaseVersion, pending.Payload) { Group = pending.Group, GroupSize = pending.GroupSize });
            }
        }

        // A group whose members changed while preparing is not sent incomplete.
        return [.. operations.Where(o => o.Group is null || operations.Count(other => other.Group == o.Group) == o.GroupSize)];
    }

    private static Dictionary<string, PushOutcome<TDocument>> CorrelateOutcomes(
        List<PushOperation<TDocument>> operations,
        PushResult<TDocument>? response)
    {
        if (response?.Outcomes is null)
        {
            throw new SyncProtocolException("The server returned no push result.");
        }

        var sent = operations.ToDictionary(static o => o.OperationId, StringComparer.Ordinal);
        var outcomes = new Dictionary<string, PushOutcome<TDocument>>(StringComparer.Ordinal);
        foreach (var outcome in response.Outcomes)
        {
            if (outcome is null || !sent.TryGetValue(outcome.OperationId, out var operation))
            {
                throw new SyncProtocolException($"The server reported an outcome for unknown operation '{outcome?.OperationId}'.");
            }

            if (!outcomes.TryAdd(outcome.OperationId, outcome))
            {
                throw new SyncProtocolException($"The server reported operation '{outcome.OperationId}' twice.");
            }

            var carriesState = outcome.Kind is PushOutcomeKind.Accepted or PushOutcomeKind.Conflict;
            if (carriesState
                && (outcome.Document is null
                    || outcome.Version is not >= 1
                    || !string.Equals(outcome.Document.Id, operation.DocumentId, StringComparison.Ordinal)))
            {
                throw new SyncProtocolException($"The server returned a malformed {outcome.Kind} outcome for '{outcome.OperationId}'.");
            }

            if (!Enum.IsDefined(outcome.Kind))
            {
                throw new SyncProtocolException($"The server returned an unknown outcome kind for '{outcome.OperationId}'.");
            }
        }

        return outcomes;
    }

    private SyncRecord<TDocument>? ApplyAccepted(SyncRecord<TDocument>? existing, string operationId, PushOutcome<TDocument> outcome, long generation)
    {
        // A stale or duplicate response for an operation that is no longer pending changes nothing.
        if (existing?.Pending is not { } pending || pending.OperationId != operationId)
        {
            return null;
        }

        // Base and current share one copy: documents in records are never mutated in place.
        var confirmed = _clone(outcome.Document!);
        var rebased = existing with
        {
            Base = confirmed,
            BaseVersion = outcome.Version,
            Pending = null,
            Generation = generation,
            Group = pending.Group is not null && existing.Group?.Id == pending.Group ? null : existing.Group,
        };

        // Only the revision that was sent becomes clean; a later local edit stays dirty on the new base.
        return Settle(existing.LocalRevision == pending.Revision
            ? rebased with { Current = confirmed, IsDirty = false, Rejection = null }
            : rebased);
    }

    private static SyncRecord<TDocument>? ApplyRejected(SyncRecord<TDocument>? existing, string operationId, PushOutcome<TDocument> outcome)
    {
        if (existing?.Pending is not { } pending || pending.OperationId != operationId)
        {
            return null;
        }

        // The server no longer knows the base: the next local edit is sent as a new document (an explicit choice
        // to recreate it), never silently.
        var forgotten = outcome.ErrorCode == PushErrorCodes.BaseExpired
            ? existing with { Pending = null, Base = null, BaseVersion = null, Observed = null, ObservedVersion = null }
            : existing with { Pending = null };
        return existing.LocalRevision == pending.Revision
            ? forgotten with { Rejection = new SyncRejection(pending.Revision, outcome.ErrorCode ?? "rejected", outcome.Message) }
            : forgotten;
    }

    /// <summary>Runs the conflict handler and commits its decision; returns whether the record is still pushable.</summary>
    private async Task<bool> ResolveConflictAsync(string operationId, PushOutcome<TDocument> outcome, long generation, CancellationToken cancellationToken)
    {
        var master = outcome.Document!;
        var masterVersion = outcome.Version!.Value;
        _clock.Update(master.UpdatedAt);

        var snapshot = await _store.GetAsync(master.Id, cancellationToken).ConfigureAwait(false);
        if (snapshot?.Pending?.OperationId != operationId)
        {
            return snapshot?.IsPushable ?? false;
        }

        var resolution = _conflictHandler.Resolve(new ConflictContext<TDocument>(
            RealMaster: _clone(master),
            AssumedMaster: snapshot.Base is { } b ? _clone(b) : null,
            Fork: _clone(snapshot.Current)));

        SyncDiagnostics.ConflictDecisions.Add(1, _nameTag, new KeyValuePair<string, object?>("bsync.decision", resolution.Outcome switch
        {
            ConflictOutcome.UseMaster => "use-master",
            ConflictOutcome.UseResolved => "use-resolved",
            ConflictOutcome.KeepFork => "keep-fork",
            ConflictOutcome.Defer => "defer",
            _ => "unknown",
        }));

        TDocument? resolved = null;
        if (resolution.Outcome == ConflictOutcome.UseResolved)
        {
            resolved = _clone(resolution.Resolved ?? throw new InvalidOperationException("A UseResolved resolution must carry a document."));
            resolved.Id = master.Id;
            resolved.UpdatedAt = _clock.Update(master.UpdatedAt);
        }

        var results = await CommitAsync(SyncChangeKind.Sync, 
            [new RecordUpdate<TDocument>(master.Id, existing =>
            {
                if (existing?.Pending?.OperationId != operationId)
                {
                    return null;
                }

                if (existing.LocalRevision != snapshot.LocalRevision)
                {
                    // Edited while resolving: drop the stale operation but keep the old base, so the
                    // next push conflicts again and the handler sees the latest local state.
                    return existing with { Pending = null };
                }

                var rebased = existing with { Base = _clone(master), BaseVersion = masterVersion, Pending = null, Generation = generation };
                return Settle(resolution.Outcome switch
                {
                    ConflictOutcome.UseMaster => rebased with { Current = _clone(master), IsDirty = false, Rejection = null },
                    ConflictOutcome.KeepFork => rebased with { IsDirty = true },
                    ConflictOutcome.Defer => rebased with
                    {
                        Current = _options.KeptConflictView == KeptConflictView.Local ? existing.Current : _clone(master),
                        IsDirty = false,
                        Rejection = null,
                        Conflict = new SyncConflict<TDocument>(_clone(master), masterVersion, _clone(existing.Current), existing.Base is { } ancestor ? _clone(ancestor) : null),
                    },
                    _ => rebased with { Current = _clone(resolved!), IsDirty = true, LocalRevision = existing.LocalRevision + 1 },
                });
            })],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return results[0].Record?.IsPushable ?? false;
    }

    private async Task<IReadOnlyList<RecordUpdateResult<TDocument>>> CommitAsync(
        SyncChangeKind kind,
        IReadOnlyList<RecordUpdate<TDocument>> updates,
        ReplicaCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var results = await _store.UpdateAsync(updates, cursor, cancellationToken).ConfigureAwait(false);
        List<string>? changed = null;
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Changed)
            {
                (changed ??= []).Add(updates[i].Id);
            }
        }

        if (changed is not null)
        {
            Notify(new SyncChange(kind, changed));
        }

        return results;
    }

    private void Notify(SyncChange change)
    {
        ImmutableObserverList observers;
        lock (_observerGate)
        {
            observers = _observers;
        }

        foreach (var observer in observers.Items)
        {
            try
            {
                observer.OnChange(change);
            }
            catch (Exception error)
            {
                try
                {
                    observer.OnError?.Invoke(error);
                }
                catch
                {
                    // An observer's error handler must not affect replication either.
                }
            }
        }
    }

    private sealed record Observer(Action<SyncChange> OnChange, Action<Exception>? OnError);

    /// <summary>Copy-on-write list so notifications never hold the lock while calling observers.</summary>
    private sealed class ImmutableObserverList(Observer[] items)
    {
        public static readonly ImmutableObserverList Empty = new([]);

        public Observer[] Items { get; } = items;

        public ImmutableObserverList Add(Observer observer) => new([.. Items, observer]);

        public ImmutableObserverList Remove(Observer observer) => new(Items.Where(o => !ReferenceEquals(o, observer)).ToArray());
    }

    private sealed class Subscription(SyncEngine<TDocument> engine, Observer observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (engine._observerGate)
                {
                    engine._observers = engine._observers.Remove(observer);
                }
            }
        }
    }
}
