using Bsync.Clocks;
using Bsync.Storage;

namespace Bsync.Tests.TestSupport;

/// <summary>
/// Wraps a store and runs a hook before each <see cref="UpdateAsync"/> is committed. The hook can
/// interleave a concurrent local edit (to model a race between a read and a commit) or throw to model a
/// crash at a commit boundary.
/// </summary>
public sealed class InterceptingStore<T>(ILocalStore<T> inner) : ILocalStore<T>
    where T : class, ISyncEntity
{
    public ILocalStore<T> Inner { get; } = inner;

    public int UpdateCalls { get; private set; }

    /// <summary>Receives the 1-based call number, the updates and the cursor.</summary>
    public Func<int, IReadOnlyList<RecordUpdate<T>>, ReplicaCursor?, Task>? BeforeUpdate { get; set; }

    public async Task<IReadOnlyList<RecordUpdateResult<T>>> UpdateAsync(
        IReadOnlyList<RecordUpdate<T>> updates,
        ReplicaCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var call = ++UpdateCalls;
        if (BeforeUpdate is { } hook)
        {
            await hook(call, updates, cursor);
        }

        return await Inner.UpdateAsync(updates, cursor, cancellationToken);
    }

    public Task<SyncRecord<T>?> GetAsync(string id, CancellationToken cancellationToken = default) => Inner.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<SyncRecord<T>>> GetPendingAsync(int limit, IReadOnlySet<string>? exclude = null, CancellationToken cancellationToken = default) =>
        Inner.GetPendingAsync(limit, exclude, cancellationToken);

    public Task<int> CountDirtyAsync(CancellationToken cancellationToken = default) => Inner.CountDirtyAsync(cancellationToken);

    public int FullQueries => Volatile.Read(ref _fullQueries);

    private int _fullQueries;

    public Task<IReadOnlyList<T>> QueryAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _fullQueries);
        return Inner.QueryAsync(includeDeleted, cancellationToken);
    }

    public Task<ReplicaCursor> GetCursorAsync(CancellationToken cancellationToken = default) => Inner.GetCursorAsync(cancellationToken);

    public Task<IReadOnlyList<SyncRecord<T>>> GetStaleAsync(long generation, int limit, CancellationToken cancellationToken = default) =>
        Inner.GetStaleAsync(generation, limit, cancellationToken);

    public Task<IReadOnlyList<SyncRecord<T>>> GetConflictsAsync(int limit, CancellationToken cancellationToken = default) =>
        Inner.GetConflictsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<SyncRecord<T>>> GetRejectedAsync(int limit, CancellationToken cancellationToken = default) =>
        Inner.GetRejectedAsync(limit, cancellationToken);

    public Task<IReadOnlyList<T>> QueryPageAsync(string? afterId, int limit, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        Inner.QueryPageAsync(afterId, limit, includeDeleted, cancellationToken);

    public Task<int> PurgeAsync(IReadOnlyList<string> ids, long generation, CancellationToken cancellationToken = default) =>
        Inner.PurgeAsync(ids, generation, cancellationToken);

    public Task<HlcTimestamp> GetClockHighWaterAsync(CancellationToken cancellationToken = default) => Inner.GetClockHighWaterAsync(cancellationToken);

    // Members with default implementations: forward them, so tests exercise the real store's version.
    public Task ResetClockHighWaterAsync(HlcTimestamp value, CancellationToken cancellationToken = default) => Inner.ResetClockHighWaterAsync(value, cancellationToken);

    public Task<int> PurgeTombstonesAsync(long throughVersion, long generation, CancellationToken cancellationToken = default) => Inner.PurgeTombstonesAsync(throughVersion, generation, cancellationToken);

    public Task<SyncIssueCounts> CountIssuesAsync(CancellationToken cancellationToken = default) => Inner.CountIssuesAsync(cancellationToken);

    public Task<IReadOnlyList<T>> QueryIndexAsync(SyncIndexQuery<T> query, SyncIndexCursor? after, int limit, CancellationToken cancellationToken = default) =>
        Inner.QueryIndexAsync(query, after, limit, cancellationToken);

    public Task<int> CountIndexAsync(SyncIndexQuery<T> query, CancellationToken cancellationToken = default) => Inner.CountIndexAsync(query, cancellationToken);
}
