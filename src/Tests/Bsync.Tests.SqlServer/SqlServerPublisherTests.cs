using System.Diagnostics;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Bsync.Tests.SqlServer;

/// <summary>Task B3: server-originated writes through <see cref="ISyncPublisher{TDocument}"/> on SQL Server.</summary>
public sealed class SqlServerPublisherTests(ITestOutputHelper output) : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await SqlServerDatabase.CreateAsync();
        await _database.ExecuteAsync("CREATE TABLE dbo.audit (id int IDENTITY PRIMARY KEY, what nvarchar(200) NOT NULL)");
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private static Note Projected(int i, string suffix = "") => new() { Id = $"p{i:00000}", Title = $"projection {i}{suffix}" };

    [Fact(DisplayName = "B3: rebuilding 10,000 unchanged documents appends no feed entries")]
    public async Task UnchangedRebuildAppendsNothing()
    {
        await using var authority = await _database.AuthorityAsync();
        var documents = Enumerable.Range(0, 10_000).Select(i => Projected(i)).ToList();

        var watch = Stopwatch.StartNew();
        var built = await authority.ReplaceScopeAsync("default", documents);
        var buildTime = watch.Elapsed;
        var head = await authority.GetHighestVersionAsync("default");
        watch.Restart();
        var rebuilt = await authority.ReplaceScopeAsync("default", Enumerable.Range(0, 10_000).Select(i => Projected(i)));
        var rebuildTime = watch.Elapsed;
        output.WriteLine($"build {buildTime.TotalSeconds:F1} s, unchanged rebuild {rebuildTime.TotalSeconds:F1} s");

        Assert.Equal(new SyncPublishResult(10_000, 0, 0), built);
        Assert.Equal(new SyncPublishResult(0, 10_000, 0), rebuilt);
        Assert.Equal(head, await authority.GetHighestVersionAsync("default"));
        Assert.Equal(10_000, await _database.ScalarAsync("SELECT count(*) FROM bsync.documents"));
    }

    [Fact(DisplayName = "B3 I10: a document removed by ReplaceScope reaches a replica as a tombstone, without a reset")]
    public async Task RemovalReachesReplicaWithoutReset()
    {
        await using var authority = await _database.AuthorityAsync();
        var replica = new TestReplica(InMemorySyncServerRef.Create(), "r", physicalClock: Clocks.SystemPhysicalClock.Instance, transport: _ => new InProcessTransport<Note>(authority));
        await authority.ReplaceScopeAsync("default", [Projected(1), Projected(2), Projected(3)]);
        await replica.Engine.SyncAsync();

        await authority.ReplaceScopeAsync("default", [Projected(1), Projected(3, " changed")]);
        var result = await replica.Engine.SyncAsync();

        Assert.False(result.ResetPerformed);
        Assert.True((await replica.RecordAsync("p00002")).Current.Deleted);
        Assert.Equal(["p00001", "p00003"], (await replica.Engine.QueryAsync()).Select(n => n.Id).Order());
        Assert.Equal("projection 3 changed", (await replica.RecordAsync("p00003")).Current.Title);
    }

    [Theory(DisplayName = "B3 ADR-014 I01: a publisher write and a domain write in one transaction commit or roll back together")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublisherEnlistsInCallerTransaction(bool commit)
    {
        await using var authority = await _database.AuthorityAsync();
        var hints = new List<AuthorityCommit>();
        authority.Committed += hints.Add;
        await using var connection = await _database.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using (var audit = new SqlCommand("INSERT INTO dbo.audit (what) VALUES ('price changed')", connection, transaction))
        {
            await audit.ExecuteNonQueryAsync();
        }

        var published = await authority.UpsertAsync("default", new Note { Id = "price", Title = "12.50" }, transaction);
        if (commit)
        {
            transaction.Commit();
            authority.NotifyCommitted(new AuthorityCommit("default", ["price"]));
        }
        else
        {
            transaction.Rollback();
        }

        Assert.Equal(new SyncPublishResult(1, 0, 0), published);
        Assert.Equal(commit ? 1 : 0, await _database.ScalarAsync("SELECT count(*) FROM dbo.audit"));
        Assert.Equal(commit, await authority.GetAsync(SyncCallContext.Anonymous, "price") is not null);
        Assert.Equal(commit ? 1 : 0, await authority.GetHighestVersionAsync("default"));
        Assert.Equal(commit ? 1 : 0, hints.Count); // no hint for uncommitted work
    }

    [Fact(DisplayName = "B3 I13: a publisher commit outside a caller's transaction is announced")]
    public async Task PublisherCommitIsAnnounced()
    {
        await using var authority = await _database.AuthorityAsync();
        var hints = new List<AuthorityCommit>();
        authority.Committed += hints.Add;

        await authority.UpsertAsync("default", new Note { Id = "n1", Title = "x" });
        await authority.UpsertAsync("default", new Note { Id = "n1", Title = "x" });

        Assert.Equal(["n1"], Assert.Single(hints).Ids); // the unchanged republish announces nothing
    }
}
