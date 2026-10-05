using Bsync.Server;
using Bsync.Transport;

namespace Bsync.Testing;

/// <summary>
/// One authority created by an <see cref="IAuthorityConformanceDriver"/>. Cases observe it only through
/// <see cref="Connect"/> (the client's view) and use the administrative operations for retention and restore.
/// </summary>
public abstract class AuthorityUnderTest : IAsyncDisposable
{
    /// <summary>The authority, called in-process. HTTP drivers serve this instance.</summary>
    public abstract ISyncAuthority<ConformanceDocument> Authority { get; }

    /// <summary>A transport that calls the authority as <paramref name="caller"/>. Default: in-process.</summary>
    public virtual ISyncTransport<ConformanceDocument> Connect(SyncCallContext caller) =>
        new InProcessTransport<ConformanceDocument>(Authority, caller);

    /// <summary>
    /// Purges tombstones of <paramref name="scope"/> with a version at or below <paramref name="throughVersion"/>
    /// and raises its retention horizon (<see cref="AuthorityCapabilities.Retention"/>).
    /// </summary>
    public virtual Task PurgeTombstonesAsync(string scope, long throughVersion, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This authority does not support tombstone retention.");

    /// <summary>
    /// Purges receipts of <paramref name="scope"/> for operations accepted at or below <paramref name="throughVersion"/>
    /// (<see cref="AuthorityCapabilities.Retention"/>).
    /// </summary>
    public virtual Task PurgeReceiptsAsync(string scope, long throughVersion, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This authority does not support receipt retention.");

    /// <summary>
    /// Starts a new epoch whose next version is above <paramref name="versionFloor"/>, as an operator does after a
    /// restore; the documents are kept (<see cref="AuthorityCapabilities.NewEpoch"/>).
    /// </summary>
    public virtual Task BeginNewEpochAsync(long versionFloor, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This authority does not support new epochs.");

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return Authority is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }
}
