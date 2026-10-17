using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Server;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class TaskApiPublicationTests : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;
    private SqlServerSyncAuthority<TaskDocument> _authority = null!;
    public async Task InitializeAsync()
    {
        _database = await SqlServerDatabase.CreateAsync();
        await TasksDb.EnsureTablesAsync(_database.ConnectionString);
        _authority = await SqlServerSyncAuthority<TaskDocument>.CreateAsync(new()
        { ConnectionString = _database.ConnectionString, Collection = "tasks", DocumentType = TasksJson.Default.TaskDocument });
    }
    public async Task DisposeAsync() { await _authority.DisposeAsync(); await _database.DisposeAsync(); }

    [Theory]
    [InlineData("before-commit")]
    [InlineData("after-commit")]
    public async Task ExecutionStrategyRetriesWholeTransactionIncludingLostCommitResponse(string failureBoundary)
    {
        var failed = false; var retries = 0;
        var recipe = new TaskApiPublication(_database.ConnectionString, _authority)
        {
            StrategyFactory = db => new TestStrategy(db, () => retries++),
            Boundary = boundary =>
            {
                if (boundary == failureBoundary && !failed) { failed = true; throw new RetryableFailure(); }
                return Task.CompletedTask;
            },
        };
        var document = await recipe.CreateAsync("team", "alice", new("task", "One creation"));
        Assert.Equal(1, retries);
        Assert.Equal(1, document.Revision);
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks"));
        var canonical = (await _authority.GetAsync(SyncCallContext.Anonymous with { Scope = "team" }, "task"))!;
        Assert.Equal("One creation", canonical.Document.Title);
        var version = canonical.Version;
        await recipe.CreateAsync("team", "alice", new("task", "One creation"));
        Assert.Equal(version, (await _authority.GetAsync(SyncCallContext.Anonymous with { Scope = "team" }, "task"))!.Version);
        await Assert.ThrowsAsync<TaskCreationConflictException>(() => recipe.CreateAsync("team", "bob", new("task", "One creation")));
        await _database.ExecuteAsync("UPDATE dbo.Tasks SET Revision=2,Title='Newer' WHERE Id='task'");
        await Assert.ThrowsAsync<TaskCreationConflictException>(() => recipe.CreateAsync("team", "alice", new("task", "One creation")));
    }

    [Fact]
    public async Task PermanentFailureRollsBackDomainAndFeed()
    {
        var recipe = new TaskApiPublication(_database.ConnectionString, _authority)
        { Boundary = _ => throw new InvalidDataException("Permanent failure") };
        await Assert.ThrowsAsync<InvalidDataException>(() => recipe.CreateAsync("team", "alice", new("task", "Never committed")));
        Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks"));
        Assert.Null(await _authority.GetAsync(SyncCallContext.Anonymous with { Scope = "team" }, "task"));
    }

    private sealed class RetryableFailure : Exception;
    private sealed class TestStrategy(DbContext context, Action retried) : ExecutionStrategy(context, 2, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) { if (exception is not RetryableFailure) return false; retried(); return true; }
    }
}
