using Bsync.Server;
using Bsync.Testing;
using Bsync.Tests.TestSupport;
using Bsync.Transport;

namespace Bsync.Tests.Conformance;

/// <summary>The same suite with every message crossing the JSON wire encoding.</summary>
public sealed class InMemoryAuthorityOverJsonConformanceTests : AuthorityConformanceTests
{
    protected override IAuthorityConformanceDriver Driver { get; } = new JsonWireDriver(new InMemoryAuthorityDriver());

    private sealed class JsonWireDriver(IAuthorityConformanceDriver inner) : IAuthorityConformanceDriver
    {
        public AuthorityCapabilities Capabilities => inner.Capabilities;

        public async Task<AuthorityUnderTest> CreateAsync(AuthorityConformanceOptions options, CancellationToken cancellationToken = default) =>
            new OverJson(await inner.CreateAsync(options, cancellationToken));
    }

    private sealed class OverJson(AuthorityUnderTest target) : AuthorityUnderTest
    {
        public override ISyncAuthority<ConformanceDocument> Authority => target.Authority;

        public override ISyncTransport<ConformanceDocument> Connect(SyncCallContext caller) =>
            new JsonWireTransport<ConformanceDocument>(target.Connect(caller), ConformanceJsonContext.Default);

        public override Task PurgeTombstonesAsync(string scope, long throughVersion, CancellationToken cancellationToken = default) =>
            target.PurgeTombstonesAsync(scope, throughVersion, cancellationToken);

        public override Task PurgeReceiptsAsync(string scope, long throughVersion, CancellationToken cancellationToken = default) =>
            target.PurgeReceiptsAsync(scope, throughVersion, cancellationToken);

        public override Task BeginNewEpochAsync(long versionFloor, CancellationToken cancellationToken = default) =>
            target.BeginNewEpochAsync(versionFloor, cancellationToken);

        public override ValueTask DisposeAsync() => target.DisposeAsync();
    }
}
