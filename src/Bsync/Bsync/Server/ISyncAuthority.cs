using Bsync.Protocol;

namespace Bsync.Server;

/// <summary>
/// The server side of the protocol for one collection (docs/protocol/v1.md §4 and §6). Implementations
/// must pass the cases in <c>Bsync.Testing.AuthorityConformance</c> (package <c>Bsync.Testing</c>).
/// </summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public interface ISyncAuthority<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>The limits this authority enforces.</summary>
    AuthorityLimits Limits { get; }

    /// <summary>Serves the next page of the caller's feed.</summary>
    /// <exception cref="SyncResetRequiredException">The checkpoint cannot be served.</exception>
    /// <exception cref="SyncProtocolException">The request is malformed.</exception>
    Task<PullResult<TDocument>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default);

    /// <summary>Decides each operation and returns one outcome per operation, in order.</summary>
    /// <exception cref="SyncTransportException">The request exceeds <see cref="Limits"/> (<c>payload-too-large</c>).</exception>
    Task<PushResult<TDocument>> PushAsync(SyncCallContext context, PushRequest<TDocument> request, CancellationToken cancellationToken = default);
}
