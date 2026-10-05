using Bsync.Protocol;
using Bsync.Server;
using Bsync.Transport;

namespace Bsync.Testing;

/// <summary>
/// Runs the cases through the HTTP binding (docs/protocol/v1.md §8): authorities come from an inner driver, the host
/// serves each one, and the cases talk to it with <see cref="HttpSyncTransport{TDocument}"/>. Retention and epoch
/// operations go to the inner driver.
/// </summary>
/// <param name="inner">Creates the authorities to serve.</param>
/// <param name="serve">Starts a server for one authority.</param>
public sealed class HttpAuthorityDriver(
    IAuthorityConformanceDriver inner,
    Func<ISyncAuthority<ConformanceDocument>, CancellationToken, Task<HttpConformanceServer>> serve) : IAuthorityConformanceDriver
{
    /// <summary>The collection name the server must map.</summary>
    public const string Collection = "conformance";

    /// <summary>The application schema id the server must accept.</summary>
    public const string SchemaId = "conformance-v1";

    /// <summary>The JSON metadata the server must use.</summary>
    public static SyncJsonTypes<ConformanceDocument> Json { get; } = SyncJsonTypes<ConformanceDocument>.From(ConformanceJsonContext.Default);

    /// <inheritdoc />
    public AuthorityCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public async Task<AuthorityUnderTest> CreateAsync(AuthorityConformanceOptions options, CancellationToken cancellationToken = default)
    {
        var target = await inner.CreateAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            return new HttpAuthorityUnderTest(target, await serve(target.Authority, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            await target.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class HttpAuthorityUnderTest(AuthorityUnderTest target, HttpConformanceServer server) : AuthorityUnderTest
    {
        public override ISyncAuthority<ConformanceDocument> Authority => target.Authority;

        public override ISyncTransport<ConformanceDocument> Connect(SyncCallContext caller) =>
            new HttpSyncTransport<ConformanceDocument>(
                server.CreateClient(caller),
                new HttpSyncTransportOptions { Collection = Collection, SchemaId = SchemaId },
                Json);

        public override Task PurgeTombstonesAsync(string scope, long throughVersion, CancellationToken cancellationToken = default) =>
            target.PurgeTombstonesAsync(scope, throughVersion, cancellationToken);

        public override Task PurgeReceiptsAsync(string scope, long throughVersion, CancellationToken cancellationToken = default) =>
            target.PurgeReceiptsAsync(scope, throughVersion, cancellationToken);

        public override Task BeginNewEpochAsync(long versionFloor, CancellationToken cancellationToken = default) =>
            target.BeginNewEpochAsync(versionFloor, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await server.DisposeAsync().ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);
        }
    }
}
