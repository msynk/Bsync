namespace Bsync.Client;

/// <summary>
/// The UI-facing API for one synchronized collection. Components and view models depend only on this interface,
/// so the same UI code works in every Blazor render mode and in native hosts; what differs between hosts is
/// described by <see cref="Capabilities"/> and by the confirmation level of each write.
/// </summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public interface ISyncCollection<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>What this host can do (offline writes, server-confirmed writes, live updates).</summary>
    SyncCapabilities Capabilities { get; }

    /// <summary>The current replication status.</summary>
    SyncStatus Status { get; }

    /// <summary>Returns the document, or <see langword="null"/> if it does not exist or is deleted.</summary>
    Task<TDocument?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns documents matching <paramref name="query"/>, bounded by <see cref="SyncQuery{TDocument}.Limit"/>. The
    /// filter and order run in memory (they are not translated to a database query). With the default order (by id), local
    /// replicas read the store in index order page by page and stop at the limit; a custom order reads the whole
    /// collection.
    /// </summary>
    Task<IReadOnlyList<TDocument>> QueryAsync(SyncQuery<TDocument>? query = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the documents <paramref name="query"/> matches, ignoring its <see cref="SyncQuery{TDocument}.Skip"/> and
    /// <see cref="SyncQuery{TDocument}.Limit"/> (ADR-018). An index range without <see cref="SyncQuery{TDocument}.Where"/>
    /// is counted by the store; otherwise the default pages through <see cref="QueryAsync"/>.
    /// </summary>
    async Task<int> CountAsync(SyncQuery<TDocument>? query = null, CancellationToken cancellationToken = default)
    {
        query = (query ?? new SyncQuery<TDocument>()) with { Limit = SyncQuery<TDocument>.MaxLimit };
        var count = 0;
        while (true)
        {
            var page = await QueryAsync(query with { Skip = count }, cancellationToken).ConfigureAwait(false);
            count += page.Count;
            if (page.Count < SyncQuery<TDocument>.MaxLimit)
            {
                return count;
            }
        }
    }

    /// <summary>Returns where one document stands (synced, pending, rejected), or <see langword="null"/> if unknown.</summary>
    Task<SyncItemStatus?> GetItemStatusAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a document. The result says how far the write is confirmed.</summary>
    Task<SyncWriteResult> SaveAsync(TDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves several documents as one dependency group: the server applies them all or none (for example an order and its
    /// lines). Local replicas commit the group at once and upload it in one request; server-connected hosts write it in one
    /// request. Set <see cref="ISyncEntity.Deleted"/> on a document to delete it as part of the group.
    /// </summary>
    Task<IReadOnlyList<SyncWriteResult>> SaveAllAsync(IReadOnlyList<TDocument> documents, CancellationToken cancellationToken = default);

    /// <summary>Deletes a document (a tombstone that replicates).</summary>
    Task<SyncWriteResult> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> documents whose local change conflicted with a newer server change
    /// and was kept for a decision (the default conflict policy). Hosts without a local replica return an empty list:
    /// their writes report <see cref="SyncConfirmation.Conflict"/> immediately instead.
    /// </summary>
    Task<IReadOnlyList<SyncDocumentConflict<TDocument>>> GetConflictsAsync(int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until <paramref name="goal"/> is reached or <paramref name="budget"/> runs out (task C4), for example
    /// <see cref="SyncGoal.Complete"/> before signing out, or <see cref="SyncGoal.Accepted"/> with a write's revision.
    /// Background replication continues either way; cancelling the wait never undoes a local write.
    /// </summary>
    Task<SyncGoalResult> SyncAsync(SyncGoal goal, TimeSpan budget, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This collection cannot wait for sync goals.");

    /// <summary>
    /// Returns a page of documents that need a decision (kept conflicts, rejections, blocked group members) and how many
    /// there are in all (task C5). Hosts without a local replica return an empty page.
    /// </summary>
    Task<SyncIssuePage> GetIssuesAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This collection does not list issues.");

    /// <summary>
    /// Resolves a kept conflict: <paramref name="resolved"/> becomes a new local change based on the newest server
    /// state known locally, and is uploaded like any other write. Returns <see cref="SyncConfirmation.NotFound"/> if
    /// the document has no unresolved conflict.
    /// </summary>
    Task<SyncWriteResult> ResolveConflictAsync(string id, TDocument resolved, CancellationToken cancellationToken = default);

    /// <summary>Drops the kept local change of a conflict; the server state stays. Returns whether a conflict existed.</summary>
    Task<bool> DiscardConflictAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a rejected change again as a new write (after the cause was fixed, for example a permission or the
    /// device clock). Returns <see cref="SyncConfirmation.NotFound"/> if the document is not rejected.
    /// </summary>
    Task<SyncWriteResult> RetryAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the unsynchronized change of a document (pending or rejected) and returns it to the newest server
    /// state known. Returns whether there was a change to discard. An upload already in flight cannot be recalled.
    /// </summary>
    Task<bool> RevertAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="onChanged"/> when documents or <see cref="Status"/> may have changed. The callback
    /// may run on any thread: Blazor components should call <c>InvokeAsync(StateHasChanged)</c>, other UIs should
    /// dispatch to their UI thread, and then re-query. Dispose the result (for example in the component's or view
    /// model's <c>Dispose</c>) to stop receiving calls.
    /// </summary>
    IDisposable Subscribe(Action onChanged);
}
