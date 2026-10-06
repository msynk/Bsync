using Bsync.Server;
using Bsync.Server.SqlServer;
using Bsync.Testing;
using Bsync.Tests.Conformance;
using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>The public authority conformance suite against SQL Server (one collection per authority, one database per class).</summary>
public sealed class SqlServerAuthorityConformanceTests(SqlServerFixture fixture) : AuthorityConformanceTests, IClassFixture<SqlServerFixture>
{
    protected override IAuthorityConformanceDriver Driver { get; } = new SqlServerDriver(fixture);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public Task Conformance(string name) => RunAsync(name);

    private sealed class SqlServerDriver(SqlServerFixture fixture) : IAuthorityConformanceDriver
    {
        public AuthorityCapabilities Capabilities => AuthorityCapabilities.All;

        public async Task<AuthorityUnderTest> CreateAsync(AuthorityConformanceOptions options, CancellationToken cancellationToken = default) =>
            new SqlServerUnderTest(await SqlServerSyncAuthority<ConformanceDocument>.CreateAsync(
                new SqlServerSyncAuthorityOptions<ConformanceDocument>
                {
                    ConnectionString = fixture.Database.ConnectionString,
                    DocumentType = ConformanceJsonContext.Default.ConformanceDocument,
                    Collection = $"c{Guid.NewGuid():N}",
                    PhysicalClock = options.Clock,
                    MaxClockSkew = options.MaxClockSkew,
                    Validator = options.Validator,
                    CanRead = options.CanRead,
                    ScopeFingerprint = options.ScopeFingerprint,
                    Readers = options.Readers,
                    PrincipalKey = options.PrincipalKey,
                },
                cancellationToken));
    }

    private sealed class SqlServerUnderTest(SqlServerSyncAuthority<ConformanceDocument> authority) : AuthorityUnderTest
    {
        public override ISyncAuthority<ConformanceDocument> Authority => authority;

        public override Task PurgeTombstonesAsync(string scope, long throughVersion, CancellationToken cancellationToken = default) =>
            authority.PurgeTombstonesAsync(scope, throughVersion, cancellationToken);

        public override Task PurgeReceiptsAsync(string scope, long throughVersion, CancellationToken cancellationToken = default) =>
            authority.PurgeReceiptsAsync(scope, throughVersion, cancellationToken);

        public override Task BeginNewEpochAsync(long versionFloor, CancellationToken cancellationToken = default) =>
            authority.BeginNewEpochAsync(versionFloor, cancellationToken);
    }
}
