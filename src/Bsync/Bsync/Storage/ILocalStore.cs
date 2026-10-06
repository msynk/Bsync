using Bsync.Clocks;

namespace Bsync.Storage;

/// <summary>
/// The local persistence contract for one synchronized collection on one replica. Implementations are
/// the platform-specific storage layer; all protocol and conflict logic lives in the <c>SyncEngine</c>.
/// </summary>
/// <remarks>
/// <para>
/// All writes go through <see cref="UpdateAsync"/>, an atomic compare-and-transform over one or more
/// records plus, optionally, the checkpoint. This is what keeps a local edit made during a network
/// round trip from being overwritten by an acknowledgement or a pull that was computed from an older
/// read (invariants I02 and I03 in <c>docs/architecture/invariants.md</c>).
/// </para>
/// <para>
/// Stores must never hand out references to their internal state: records passed in or returned are
/// independent copies.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public interface ILocalStore<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>Returns the stored record for <paramref name="id"/>, or <see langword="null"/> if absent.</summary>
    Task<SyncRecord<TDocument>?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically applies <paramref name="updates"/> and, when <paramref name="cursor"/> is not
    /// <see langword="null"/>, stores it as the new replica cursor. Either every change becomes durable
    /// together or none does.
    /// </summary>
    /// <param name="updates">
    /// One entry per distinct record id. Each transform receives an independent copy of the record's
    /// state at commit time (or <see langword="null"/> if absent) and returns the new state, or
    /// <see langword="null"/> to leave the record unchanged. Transforms must be pure and fast, must not
    /// call the store, and may be invoked more than once by optimistic implementations.
    /// </param>
    /// <param name="cursor">The replica cursor (checkpoint, generation) to commit with the updates, if any.</param>
    /// <param name="cancellationToken">Cancels the operation before it commits.</param>
    /// <returns>One result per update, in order.</returns>
    Task<IReadOnlyList<RecordUpdateResult<TDocument>>> UpdateAsync(
        IReadOnlyList<RecordUpdate<TDocument>> updates,
        ReplicaCursor? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> pushable records (<see cref="SyncRecord{TDocument}.IsPushable"/>)
    /// ordered by their current <see cref="ISyncEntity.UpdatedAt"/> then id, skipping ids in
    /// <paramref name="exclude"/>.
    /// </summary>
    Task<IReadOnlyList<SyncRecord<TDocument>>> GetPendingAsync(
        int limit,
        IReadOnlySet<string>? exclude = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the number of dirty records (pushable or rejected).</summary>
    Task<int> CountDirtyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Physically removes clean tombstones of <paramref name="generation"/> whose server version is at or below
    /// <paramref name="throughVersion"/> (task D4: the server already purged them). Records with local changes or a kept
    /// conflict are never removed. The default enumerates; stores override it with one statement. Returns the number removed.
    /// </summary>
    async Task<int> PurgeTombstonesAsync(long throughVersion, long generation, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        foreach (var document in await QueryAsync(includeDeleted: true, cancellationToken).ConfigureAwait(false))
        {
            if (document.Deleted
                && await GetAsync(document.Id, cancellationToken).ConfigureAwait(false) is { IsDirty: false, Conflict: null, BaseVersion: { } version } record
                && version <= throughVersion
                && record.Generation == generation)
            {
                ids.Add(document.Id);
            }
        }

        return ids.Count == 0 ? 0 : await PurgeAsync(ids, generation + 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts records that need a decision: kept conflicts and parked rejections (task C5). The default enumerates them;
    /// stores override it with a count.
    /// </summary>
    async Task<SyncIssueCounts> CountIssuesAsync(CancellationToken cancellationToken = default)
    {
        var conflicts = await GetConflictsAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var rejected = await GetRejectedAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        return new SyncIssueCounts(conflicts.Count, rejected.Count, rejected.Count(static r => r.Rejection?.ErrorCode == Protocol.PushErrorCodes.GroupFailed));
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> clean records that are not marked
    /// <see cref="SyncRecord{TDocument}.MissingAfterReset"/> and whose
    /// <see cref="SyncRecord{TDocument}.Generation"/> is lower than <paramref name="generation"/>: the
    /// records a completed resnapshot did not see.
    /// </summary>
    Task<IReadOnlyList<SyncRecord<TDocument>>> GetStaleAsync(
        long generation,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Returns up to <paramref name="limit"/> records with an unresolved <see cref="SyncRecord{TDocument}.Conflict"/>, in id order.</summary>
    Task<IReadOnlyList<SyncRecord<TDocument>>> GetConflictsAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> records whose latest local change the server rejected
    /// (<see cref="SyncRecord{TDocument}.Rejection"/> is set), in id order.
    /// </summary>
    Task<IReadOnlyList<SyncRecord<TDocument>>> GetRejectedAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Physically removes the listed records that are still clean and belong to a generation older than
    /// <paramref name="generation"/> (records a completed resnapshot did not see). Dirty records and records that keep
    /// an unresolved <see cref="SyncRecord{TDocument}.Conflict"/> are never removed: both hold local changes.
    /// Atomic; returns the number removed.
    /// </summary>
    Task<int> PurgeAsync(IReadOnlyList<string> ids, long generation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the app-visible documents. By default soft-deleted records are excluded; pass
    /// <paramref name="includeDeleted"/> to include them. Records marked
    /// <see cref="SyncRecord{TDocument}.MissingAfterReset"/> are never returned.
    /// </summary>
    Task<IReadOnlyList<TDocument>> QueryAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> app-visible documents with an id ordinally greater than
    /// <paramref name="afterId"/> (all when <see langword="null"/>), in ordinal id order, using an index, so a caller can
    /// read a large collection in bounded pages. Same visibility rules as <see cref="QueryAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TDocument>> QueryPageAsync(string? afterId, int limit, bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>Gets the replica cursor (pull checkpoint and generation) for this collection.</summary>
    Task<ReplicaCursor> GetCursorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the greatest <see cref="ISyncEntity.UpdatedAt"/> ever committed to this store, so a
    /// restarted replica can seed its clock above every timestamp it may already have issued.
    /// </summary>
    Task<HlcTimestamp> GetClockHighWaterAsync(CancellationToken cancellationToken = default);
}
