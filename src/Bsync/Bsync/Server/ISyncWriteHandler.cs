namespace Bsync.Server;

/// <summary>
/// Application logic that runs inside an authority's transaction when a replicated write is about to be accepted
/// (ADR-014): recompute fields, enforce rules that need other rows, and write the application's own tables so they
/// commit atomically with the feed.
/// </summary>
/// <remarks>
/// <para>
/// Called once per operation that passed the protocol checks (clock skew, retention, <c>CanWrite</c>, <c>Validator</c>,
/// base version). Never called for a replayed operation: the stored outcome is returned instead (I04, I11).
/// </para>
/// <para>
/// Do not commit, roll back or close <see cref="SyncWriteContext{TDocument}.Transaction"/>, and do not hold it across
/// network calls: the feed is locked for the whole request. Return <see cref="SyncWriteDecision{TDocument}.RetryLater"/>
/// instead. An exception fails the whole request, which the client retries with the same operation ids.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public interface ISyncWriteHandler<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>Decides one write.</summary>
    ValueTask<SyncWriteDecision<TDocument>> HandleAsync(SyncWriteContext<TDocument> write, CancellationToken cancellationToken);
}
