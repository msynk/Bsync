using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Bsync.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Bsync.Tests.SqlServer;

/// <summary>Task C1 / ADR-015 on SQL Server: membership pulls at scale and the schema upgrade that adds them.</summary>
public sealed class SqlServerMembershipTests(ITestOutputHelper output) : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;

    public async Task InitializeAsync() => _database = await SqlServerDatabase.CreateAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private static SyncCallContext User(string name) => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test")), "team");

    private Task<SqlServerSyncAuthority<Note>> AuthorityAsync() => _database.AuthorityAsync(configure: o => new()
    {
        ConnectionString = o.ConnectionString,
        DocumentType = o.DocumentType,
        Collection = o.Collection,
        PhysicalClock = o.PhysicalClock,
        Readers = note => note.Body.Split(',', StringSplitOptions.RemoveEmptyEntries),
        PrincipalKey = caller => caller.Principal.FindFirst(ClaimTypes.Name)?.Value,
    });

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
        await _database.ExecuteAsync("DROP TABLE bsync.document_access; UPDATE bsync.meta SET value = '1' WHERE [key] = 'schema_version';");

        await using var upgraded = await _database.AuthorityAsync();
        var replay = await upgraded.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([operation]));

        Assert.Equal(2, await _database.ScalarAsync("SELECT CAST(value AS int) FROM bsync.meta WHERE [key] = 'schema_version'"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM sys.tables WHERE name = 'document_access'"));
        Assert.Equal("kept", (await upgraded.GetAsync(SyncCallContext.Anonymous, "n1"))!.Document.Title);
        Assert.True(replay.Outcomes.Single().IsDuplicate); // the receipt survived the upgrade
    }

    /// <summary>Loads documents and access rows directly, as the authority would have written them: alice reads every nth, bob reads all.</summary>
    private async Task BulkLoadAsync(int count, int aliceEvery)
    {
        await using var connection = await _database.OpenAsync();
        await using (var feed = new SqlCommand(
            "INSERT INTO bsync.feeds (collection_key, scope_key, collection, scope, sequence, purged_through) VALUES (@c, @s, 'notes', 'team', @n, 0)",
            connection))
        {
            feed.Parameters.Add("@c", SqlDbType.VarBinary, 512).Value = Encoding.BigEndianUnicode.GetBytes("notes");
            feed.Parameters.Add("@s", SqlDbType.VarBinary, 512).Value = Encoding.BigEndianUnicode.GetBytes("team");
            feed.Parameters.Add("@n", SqlDbType.BigInt).Value = (long)count;
            await feed.ExecuteNonQueryAsync();
        }

        var feedId = (int)(await new SqlCommand("SELECT feed_id FROM bsync.feeds WHERE scope = 'team'", connection).ExecuteScalarAsync())!;
        var documents = new DataTable();
        foreach (var (name, type) in new[] { ("feed_id", typeof(int)), ("id_key", typeof(byte[])), ("id", typeof(string)), ("version", typeof(long)), ("deleted", typeof(bool)), ("document", typeof(string)) })
        {
            documents.Columns.Add(name, type);
        }

        var access = new DataTable();
        foreach (var (name, type) in new[] { ("feed_id", typeof(int)), ("principal_key", typeof(byte[])), ("version", typeof(long)), ("id_key", typeof(byte[])), ("id", typeof(string)), ("granted", typeof(bool)) })
        {
            access.Columns.Add(name, type);
        }

        var alice = Encoding.BigEndianUnicode.GetBytes("alice");
        var bob = Encoding.BigEndianUnicode.GetBytes("bob");
        for (var i = 1; i <= count; i++)
        {
            var id = $"d{i:000000}";
            var key = Encoding.BigEndianUnicode.GetBytes(id);
            var readers = i % aliceEvery == 0 ? "alice,bob" : "bob";
            var note = new Note { Id = id, Title = "t", Body = readers, UpdatedAt = new HlcTimestamp(1_000, 0, "n") };
            documents.Rows.Add(feedId, key, id, (long)i, false, JsonSerializer.Serialize(note, NoteJsonContext.Default.Note));
            access.Rows.Add(feedId, bob, (long)i, key, id, true);
            if (i % aliceEvery == 0)
            {
                access.Rows.Add(feedId, alice, (long)i, key, id, true);
            }
        }

        foreach (var (table, data) in new[] { ("bsync.documents", documents), ("bsync.document_access", access) })
        {
            using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, null) { DestinationTableName = table, BulkCopyTimeout = 300, BatchSize = 20_000 };
            foreach (DataColumn column in data.Columns)
            {
                copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await copy.WriteToServerAsync(data);
        }
    }
}
