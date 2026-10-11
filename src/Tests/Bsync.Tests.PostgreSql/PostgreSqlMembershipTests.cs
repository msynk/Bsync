using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.PostgreSql;
using Bsync.Tests.TestSupport;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Bsync.Tests.PostgreSql;

/// <summary>Task C1 / ADR-015 on PostgreSQL: membership pulls at scale and the schema upgrade that adds them.</summary>
public sealed class PostgreSqlMembershipTests(ITestOutputHelper output) : IAsyncLifetime
{
    private PostgresDatabase _database = null!;

    public async Task InitializeAsync() => _database = await PostgresDatabase.CreateAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private static SyncCallContext User(string name) => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test")), "team");

    private Task<PostgreSqlSyncAuthority<Note>> AuthorityAsync() => _database.AuthorityAsync(configure: o => new()
    {
        DataSource = o.DataSource,
        DocumentType = o.DocumentType,
        Collection = o.Collection,
        PhysicalClock = o.PhysicalClock,
        Readers = note => note.Body.Split(',', StringSplitOptions.RemoveEmptyEntries),
        PrincipalKey = caller => caller.Principal.FindFirst(ClaimTypes.Name)?.Value,
    });

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var command = _database.DataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact(DisplayName = "C1 F2 I08: a filtered pull over 100,000 documents of which the caller sees 500 returns full pages")]
    public async Task FullPagesAtScale()
    {
        await using var authority = await AuthorityAsync();
        await BulkLoadAsync(100_000, aliceEvery: 200);

        var pages = new List<int>();
        var checkpoint = Checkpoint.Start;
        var watch = Stopwatch.StartNew();
        PullResult<Note> page;
        do
        {
            page = await authority.PullAsync(User("alice"), new PullRequest(checkpoint, 100) { Features = [SyncFeatures.Removals] });
            pages.Add(page.Changes.Count);
            checkpoint = page.Checkpoint;
        }
        while (page.HasMore);
        output.WriteLine($"alice: {pages.Count} pages in {watch.ElapsedMilliseconds} ms");

        Assert.Equal(500, pages.Sum());
        Assert.All(pages.SkipLast(1), count => Assert.Equal(100, count)); // full pages, not sparse ones
        Assert.InRange(pages.Count, 5, 6);
        Assert.Equal(1000, (await authority.PullAsync(User("bob"), new PullRequest(Checkpoint.Start, 1000))).Changes.Count);
    }

    [Fact(DisplayName = "C1 I17: a schema 1 database upgrades in place to schema 2 and keeps its documents and receipts")]
    public async Task SchemaOneUpgrades()
    {
        var operation = new PushOperation<Note>("o1", "n1", null, new Note { Id = "n1", Title = "kept", UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "n") });
        await using (var first = await _database.AuthorityAsync())
        {
            await first.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([operation]));
        }

        // Turn the database back into schema 1, as 0.2.0 left it.
        await ExecuteAsync("DROP TABLE bs_document_access; UPDATE bs_meta SET value = '1' WHERE key = 'schema_version';");

        await using var upgraded = await _database.AuthorityAsync();
        var replay = await upgraded.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([operation]));

        Assert.Equal(2, await ScalarAsync("SELECT CAST(value AS int) FROM bs_meta WHERE key = 'schema_version'"));
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM pg_tables WHERE tablename = 'bs_document_access'"));
        Assert.Equal("kept", (await upgraded.GetAsync(SyncCallContext.Anonymous, "n1"))!.Document.Title);
        Assert.True(replay.Outcomes.Single().IsDuplicate); // the receipt survived the upgrade
    }

    /// <summary>Loads documents and access rows directly, as the authority would have written them: alice reads every nth, bob reads all.</summary>
    private async Task BulkLoadAsync(int count, int aliceEvery)
    {
        await ExecuteAsync($"INSERT INTO bs_feeds (collection, scope, sequence, purged_through) VALUES ('notes', 'team', {count}, 0)");
        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await using (var documents = await connection.BeginBinaryImportAsync("COPY bs_documents (collection, scope, id, id_key, version, deleted, document) FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 1; i <= count; i++)
            {
                var id = $"d{i:000000}";
                var note = new Note { Id = id, Title = "t", Body = i % aliceEvery == 0 ? "alice,bob" : "bob", UpdatedAt = new HlcTimestamp(1_000, 0, "n") };
                await documents.StartRowAsync();
                await documents.WriteAsync("notes");
                await documents.WriteAsync("team");
                await documents.WriteAsync(id);
                await documents.WriteAsync(System.Text.Encoding.BigEndianUnicode.GetBytes(id));
                await documents.WriteAsync((long)i);
                await documents.WriteAsync(false);
                await documents.WriteAsync(JsonSerializer.Serialize(note, NoteJsonContext.Default.Note));
            }

            await documents.CompleteAsync();
        }

        await using (var access = await connection.BeginBinaryImportAsync("COPY bs_document_access (collection, scope, principal_key, version, id, granted) FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 1; i <= count; i++)
            {
                var id = $"d{i:000000}";
                foreach (var principal in i % aliceEvery == 0 ? new[] { "alice", "bob" } : ["bob"])
                {
                    await access.StartRowAsync();
                    await access.WriteAsync("notes");
                    await access.WriteAsync("team");
                    await access.WriteAsync(principal);
                    await access.WriteAsync((long)i);
                    await access.WriteAsync(id);
                    await access.WriteAsync(true);
                }
            }

            await access.CompleteAsync();
        }
    }
}
