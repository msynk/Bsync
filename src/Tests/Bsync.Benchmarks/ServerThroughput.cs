using System.Diagnostics;
using System.Security.Claims;
using Bsync;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Server;
using Bsync.Server.PostgreSql;
using Bsync.Server.SqlServer;
using Bsync.Storage;
using Microsoft.Data.SqlClient;
using Npgsql;

/// <summary>
/// Task H: authority throughput with concurrent sessions. Not a BenchmarkDotNet benchmark (each run needs a fresh
/// database): <c>throughput sqlserver|postgres "connection string" [sessions] [documents per session]</c>.
/// Every session is an engine on an in-memory store calling the authority in-process (no HTTP), so the numbers are the
/// authority's and the database's.
/// </summary>
public static class ServerThroughput
{
    public static async Task RunAsync(string provider, string connectionString, int sessions, int documents)
    {
        Console.WriteLine($"provider={provider} sessions={sessions} documents/session={documents} document~1KiB pushBatch=100 pullBatch=500");
        foreach (var sharedFeed in new[] { true, false })
        {
            await using var database = await TemporaryDatabase.CreateAsync(provider, connectionString);
            await using var authority = await database.AuthorityAsync();
            var contexts = Enumerable.Range(0, sessions)
                .Select(i => sharedFeed ? SyncCallContext.Anonymous : new SyncCallContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, $"u{i}")], "bench")), $"tenant-{i}"))
                .ToList();

            // Push: every session writes its documents locally, then uploads them concurrently with the others.
            var writers = new List<SyncEngine<BenchDocument>>();
            for (var s = 0; s < sessions; s++)
            {
                var engine = Engine(authority, contexts[s], $"w{s}");
                for (var d = 0; d < documents; d++)
                {
                    var document = Workload.Document((s * documents) + d);
                    await engine.WriteAsync(document);
                }

                writers.Add(engine);
            }

            var push = Stopwatch.StartNew();
            var pushed = (await Task.WhenAll(writers.Select(w => Task.Run(() => w.PushAsync())))).Sum(r => r.Pushed);
            push.Stop();

            // Pull: as many new replicas pull everything they can see, concurrently.
            var readers = contexts.Select((c, i) => Engine(authority, c, $"r{i}")).ToList();
            var pull = Stopwatch.StartNew();
            var pulled = (await Task.WhenAll(readers.Select(r => Task.Run(() => r.PullAsync())))).Sum(r => r.Pulled);
            pull.Stop();

            Console.WriteLine(
                $"{(sharedFeed ? "one shared feed" : "one feed per session")}: pushed {pushed} in {push.Elapsed.TotalSeconds:0.00} s = {pushed / push.Elapsed.TotalSeconds:0} operations/s; " +
                $"pulled {pulled} in {pull.Elapsed.TotalSeconds:0.00} s = {pulled / pull.Elapsed.TotalSeconds:0} changes/s");
        }
    }

    private static SyncEngine<BenchDocument> Engine(ISyncAuthority<BenchDocument> authority, SyncCallContext context, string node) =>
        new(new InMemoryLocalStore<BenchDocument>(Workload.Clone), new InProcessTransport<BenchDocument>(authority, context), new HybridLogicalClock(node), Workload.Clone,
            options: new SyncOptions<BenchDocument> { PushBatchSize = 100, PullBatchSize = 500, MaxPushBatches = 10_000, MaxPullPages = 10_000 });

    /// <summary>A database created for one run and dropped afterwards.</summary>
    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _provider;
        private readonly string _admin;
        private readonly string _name;
        private NpgsqlDataSource? _source;

        private TemporaryDatabase(string provider, string admin, string name, string connectionString)
        {
            _provider = provider;
            _admin = admin;
            _name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<TemporaryDatabase> CreateAsync(string provider, string admin)
        {
            var name = $"bsync_bench_{Guid.NewGuid():N}";
            if (provider == "sqlserver")
            {
                await using var connection = new SqlConnection(admin);
                await connection.OpenAsync();
                await new SqlCommand($"CREATE DATABASE [{name}]", connection).ExecuteNonQueryAsync();
                return new TemporaryDatabase(provider, admin, name, new SqlConnectionStringBuilder(admin) { InitialCatalog = name }.ConnectionString);
            }

            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
            }

            return new TemporaryDatabase(provider, admin, name, new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString);
        }

        public async Task<IAsyncDisposableAuthority> AuthorityAsync()
        {
            if (_provider == "sqlserver")
            {
                return new IAsyncDisposableAuthority(await SqlServerSyncAuthority<BenchDocument>.CreateAsync(new SqlServerSyncAuthorityOptions<BenchDocument>
                {
                    ConnectionString = ConnectionString,
                    DocumentType = BenchJson.Default.BenchDocument,
                    Collection = "bench",
                    MaxOperationsPerPush = 1000,
                    MaxPageSize = 1000,
                }));
            }

            _source = NpgsqlDataSource.Create(ConnectionString);
            return new IAsyncDisposableAuthority(await PostgreSqlSyncAuthority<BenchDocument>.CreateAsync(new PostgreSqlSyncAuthorityOptions<BenchDocument>
            {
                DataSource = _source,
                DocumentType = BenchJson.Default.BenchDocument,
                Collection = "bench",
                MaxOperationsPerPush = 1000,
                MaxPageSize = 1000,
            }));
        }

        public async ValueTask DisposeAsync()
        {
            if (_source is not null)
            {
                await _source.DisposeAsync();
            }

            if (_provider == "sqlserver")
            {
                SqlConnection.ClearAllPools();
                await using var connection = new SqlConnection(_admin);
                await connection.OpenAsync();
                await new SqlCommand($"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]", connection).ExecuteNonQueryAsync();
            }
            else
            {
                NpgsqlConnection.ClearAllPools();
                await using var connection = new NpgsqlConnection(_admin);
                await connection.OpenAsync();
                await new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
            }
        }
    }

    /// <summary>An authority that is disposed with the run.</summary>
    private sealed class IAsyncDisposableAuthority(ISyncAuthority<BenchDocument> inner) : ISyncAuthority<BenchDocument>, IAsyncDisposable
    {
        public AuthorityLimits Limits => inner.Limits;

        public Task<Bsync.Protocol.PullResult<BenchDocument>> PullAsync(SyncCallContext context, Bsync.Protocol.PullRequest request, CancellationToken cancellationToken = default) =>
            inner.PullAsync(context, request, cancellationToken);

        public Task<Bsync.Protocol.PushResult<BenchDocument>> PushAsync(SyncCallContext context, Bsync.Protocol.PushRequest<BenchDocument> request, CancellationToken cancellationToken = default) =>
            inner.PushAsync(context, request, cancellationToken);

        public ValueTask DisposeAsync() => inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }
}
