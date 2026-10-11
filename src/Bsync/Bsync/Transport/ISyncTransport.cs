using Bsync.Protocol;

namespace Bsync.Transport;

/// <summary>
/// The client-side view of the server for a single collection. Implementations carry the protocol
/// messages over a wire (HTTP for pull/push, SignalR or SSE for the live stream) or, in tests, call
/// an in-process server directly. The engine depends only on this abstraction.
/// </summary>
/// <remarks>
/// Documents in push results belong to the caller: the engine may keep them without copying, so an implementation
/// must not keep, reuse or change them afterwards. Wire transports deserialize new ones; an in-process transport
/// passes on what its authority returns, and the included authorities return copies.
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public interface ISyncTransport<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>Pulls the next page of server changes after the request's checkpoint.</summary>
    Task<PullResult<TDocument>> PullAsync(PullRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a batch of operations and returns their outcomes. If this throws, or is cancelled, after
    /// the request may have reached the server, the outcome is unknown: the engine keeps the operations
    /// pending and resends them with the same ids.
    /// </summary>
    Task<PushResult<TDocument>> PushAsync(PushRequest<TDocument> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to the server's live change stream. <see cref="Client.SyncSession{TDocument}"/> consumes it as hints
    /// when <see cref="Client.SyncSessionOptions{TDocument}.LiveHints"/> is set; the engine itself does not. Events are
    /// hints only: losing them never loses data (I13). Implementations that do not support live streaming may throw
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    IAsyncEnumerable<StreamEvent<TDocument>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default);
}
