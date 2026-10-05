using Bsync.Server;
using Bsync.Server.PostgreSql;
using Bsync.Testing;
using Bsync.Tests.Conformance;
using Xunit;

namespace Bsync.Tests.PostgreSql;

/// <summary>The public authority conformance suite against PostgreSQL (one collection per authority, one database per class).</summary>
public sealed class PostgreSqlAuthorityConformanceTests(PostgresFixture fixture) : AuthorityConformanceTests, IClassFixture<PostgresFixture>
{
    protected override IAuthorityConformanceDriver Driver { get; } = new PostgreSqlDriver(fixture);

    private sealed class PostgreSqlDriver(PostgresFixture fixture) : IAuthorityConformanceDriver
    {
        public AuthorityCapabilities Capabilities => AuthorityCapabilities.All;

        public async Task<AuthorityUnderTest> CreateAsync(AuthorityConformanceOptions options, CancellationToken cancellationToken = default) =>
            new PostgreSqlUnderTest(await PostgreSqlSyncAuthority<ConformanceDocument>.CreateAsync(
                new PostgreSqlSyncAuthorityOptions<ConformanceDocument>
                {
                    DataSource = fixture.Database.DataSource,
                    DocumentType = ConformanceJsonContext.Default.ConformanceDocument,
                    Collection = $"c{Guid.NewGuid():N}",
                    PhysicalClock = options.Clock,
                    MaxClockSkew = options.MaxClockSkew,
                    Validator = options.Validator,
                    CanRead = options.CanRead,
                    ScopeFingerprint = options.ScopeFingerprint,
                },
                cancellationToken));
    }

    private sealed class PostgreSqlUnderTest(PostgreSqlSyncAuthority<ConformanceDocument> authority) : AuthorityUnderTest
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
