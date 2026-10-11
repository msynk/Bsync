using Bsync.Storage;
using Bsync.Testing;
using Bsync.Tests.Conformance;

namespace Bsync.Tests.Sqlite;

/// <summary>Writes through one store instance and reads through another sharing the same storage.</summary>
public sealed class SplitStore<T>(ILocalStore<T> writer, ILocalStore<T> reader) : ILocalStore<T>
    where T : class, ISyncEntity
{
    public Task<SyncRecord<T>?> GetAsync(string id, CancellationToken cancellationToken = default) => reader.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<RecordUpdateResult<T>>> UpdateAsync(IReadOnlyList<RecordUpdate<T>> updates, ReplicaCursor? cursor = null, CancellationToken cancellationToken = default) =>
        writer.UpdateAsync(updates, cursor, cancellationToken);

    public Task<IReadOnlyList<SyncRecord<T>>> GetPendingAsync(int limit, IReadOnlySet<string>? exclude = null, CancellationToken cancellationToken = default) =>
        reader.GetPendingAsync(limit, exclude, cancellationToken);

    public Task<int> CountDirtyAsync(CancellationToken cancellationToken = default) => reader.CountDirtyAsync(cancellationToken);

    public Task<IReadOnlyList<SyncRecord<T>>> GetStaleAsync(long generation, int limit, CancellationToken cancellationToken = default) =>
        reader.GetStaleAsync(generation, limit, cancellationToken);

    public Task<IReadOnlyList<SyncRecord<T>>> GetConflictsAsync(int limit, CancellationToken cancellationToken = default) =>
        reader.GetConflictsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<SyncRecord<T>>> GetRejectedAsync(int limit, CancellationToken cancellationToken = default) =>
        reader.GetRejectedAsync(limit, cancellationToken);

    public Task<IReadOnlyList<T>> QueryPageAsync(string? afterId, int limit, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        reader.QueryPageAsync(afterId, limit, includeDeleted, cancellationToken);

    public Task<int> PurgeAsync(IReadOnlyList<string> ids, long generation, CancellationToken cancellationToken = default) =>
        writer.PurgeAsync(ids, generation, cancellationToken);

    public Task<IReadOnlyList<T>> QueryAsync(bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        reader.QueryAsync(includeDeleted, cancellationToken);

    public Task<IReadOnlyList<T>> QueryIndexAsync(SyncIndexQuery<T> query, SyncIndexCursor? after, int limit, CancellationToken cancellationToken = default) =>
        reader.QueryIndexAsync(query, after, limit, cancellationToken);

    public Task<int> CountIndexAsync(SyncIndexQuery<T> query, CancellationToken cancellationToken = default) =>
        reader.CountIndexAsync(query, cancellationToken);

    public Task ResetClockHighWaterAsync(Bsync.Clocks.HlcTimestamp value, CancellationToken cancellationToken = default) =>
        writer.ResetClockHighWaterAsync(value, cancellationToken);

    public Task<ReplicaCursor> GetCursorAsync(CancellationToken cancellationToken = default) => reader.GetCursorAsync(cancellationToken);

    public Task<Clocks.HlcTimestamp> GetClockHighWaterAsync(CancellationToken cancellationToken = default) => reader.GetClockHighWaterAsync(cancellationToken);
}
