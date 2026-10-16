using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Server;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class ProjectionOutboxTests : IAsyncLifetime
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
    private async Task Write(string id, string title, bool commit = true, bool deleted = false)
    {
        await using var connection = await _database.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await _authority.AcquirePublicationLockAsync("team", transaction);
        await using var command = new SqlCommand("""
            UPDATE dbo.Tasks SET Title=@title,Revision=Revision+1,Deleted=@deleted WHERE Tenant='team' AND Id=@id;
            IF @@ROWCOUNT=0 INSERT dbo.Tasks(Tenant,Id,Title,Slug,Done,Deleted,Revision) VALUES('team',@id,@title,@title,0,@deleted,1);
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@title", title); command.Parameters.AddWithValue("@deleted", deleted);
        await command.ExecuteNonQueryAsync();
        await TaskProjectionOutbox.RequestAsync(connection, transaction, "team", id);
        if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
    }
    [Fact]
    public async Task CommandAndOutboxKeysSupportTheirDeclaredFullLengths()
    {
        await using var connection = await _database.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var tenant = new string('t', 256); var id = new string('i', 256);
        await _authority.AcquirePublicationLockAsync(tenant, transaction);
        await TaskProjectionOutbox.RequestAsync(connection, transaction, tenant, id);
        await using var ledger = new SqlCommand("INSERT dbo.IntentExecutions(Tenant,CommandId,RequestHash,ResultExpiresAt) VALUES(@tenant,@id,@hash,SYSUTCDATETIME())", connection, transaction);
        ledger.Parameters.AddWithValue("@tenant", tenant); ledger.Parameters.AddWithValue("@id", id); ledger.Parameters.AddWithValue("@hash", new string('0', 64));
        Assert.Equal(1, await ledger.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.IntentExecutions"));
    }

    [Fact]
    public async Task CoalescingPreservesOldestUnpublishedRequestAge()
    {
        await Write("task", "old");
        await _database.ExecuteAsync("UPDATE dbo.TaskProjectionOutbox SET RequestedAt='2020-01-01T00:00:00'");
        await Write("task", "latest");
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox WHERE RequestedAt='2020-01-01T00:00:00'"));
        Assert.Equal(1, await new TaskProjectionOutbox(_database.ConnectionString, _authority).DrainAsync());
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
    }

    [Fact]
    public async Task RollbackCoalescingRestartAndDeletionHaveExplicitEventualPublication()
    {
        await Write("rollback", "never", commit: false);
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.Tasks"));
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
        await Write("task", "old"); await Write("task", "latest");
        var caller = SyncCallContext.Anonymous with { Scope = "team" };
        Assert.Null(await _authority.GetAsync(caller, "task"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
        var dispatcher = new TaskProjectionOutbox(_database.ConnectionString, _authority);
        Assert.Equal(1, await dispatcher.DrainAsync());
        Assert.Equal("latest", (await _authority.GetAsync(caller, "task"))!.Document.Title);
        Assert.Equal(0, await new TaskProjectionOutbox(_database.ConnectionString, _authority).DrainAsync());
        await Write("task", "latest", deleted: true);
        Assert.Equal(1, await dispatcher.DrainAsync());
        Assert.True((await _authority.GetAsync(caller, "task"))!.Document.Deleted);
    }
    [Fact]
    public async Task PoisonProjectionIsRetainedWhileIndependentWorkProgresses()
    {
        await Write("", "invalid document identity"); await Write("healthy", "valid");
        var dispatcher = new TaskProjectionOutbox(_database.ConnectionString, _authority);
        Assert.Equal(1, await dispatcher.DrainAsync());
        Assert.NotNull(await _authority.GetAsync(SyncCallContext.Anonymous with { Scope = "team" }, "healthy"));
        for (var i = 0; i < 4; i++)
        {
            await _database.ExecuteAsync("UPDATE dbo.TaskProjectionOutbox SET RetryAt=NULL");
            Assert.Equal(0, await dispatcher.DrainAsync());
        }
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox WHERE Poisoned=1 AND Attempts=5"));
    }

    [Fact]
    public async Task BoundedFullReconciliationRepairsMissingChangedAndDeletedProjectionsAtomically()
    {
        await Write("one", "old"); await Write("removed", "old");
        await new TaskProjectionOutbox(_database.ConnectionString, _authority).DrainAsync();
        await Write("one", "latest"); await Write("new", "new"); await Write("removed", "old", deleted: true);
        var repair = new TaskProjectionReconciler(_database.ConnectionString, _authority);
        var caller = SyncCallContext.Anonymous with { Scope = "team" };
        // The bounded read must refuse a partial snapshot rather than tombstoning the unseen tail.
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair.ReconcileAsync("team", maximumTasks: 1));
        Assert.Equal("old", (await _authority.GetAsync(caller, "one"))!.Document.Title);
        Assert.False((await _authority.GetAsync(caller, "removed"))!.Document.Deleted);
        Assert.Equal(3, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
        var result = await repair.ReconcileAsync("team");
        Assert.Equal(2, result.Written);
        Assert.Equal(1, result.Deleted);
        Assert.Equal("latest", (await _authority.GetAsync(caller, "one"))!.Document.Title);
        Assert.NotNull(await _authority.GetAsync(caller, "new"));
        Assert.True((await _authority.GetAsync(caller, "removed"))!.Document.Deleted);
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
        Assert.Equal(0, (await repair.ReconcileAsync("team")).Written);
    }

    [Fact]
    public async Task ReconciliationWaitsForDomainWriterAndCapturesItsCommittedAttachments()
    {
        await Write("task", "old");
        await new TaskProjectionOutbox(_database.ConnectionString, _authority).DrainAsync();
        await using var connection = await _database.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await _authority.AcquirePublicationLockAsync("team", transaction);
        await using var change = new SqlCommand("""
            UPDATE dbo.Tasks SET Title=N'concurrent' WHERE Tenant=N'team' AND Id=N'task';
            INSERT dbo.TaskAttachments(Tenant,TaskId,AttachmentId,Sha256,Size,ContentType,FileName)
                VALUES(N'team',N'task',N'file',REPLICATE('a',64),1,N'text/plain',N'page.txt');
            """, connection, transaction);
        await change.ExecuteNonQueryAsync();
        await TaskProjectionOutbox.RequestAsync(connection, transaction, "team", "task");
        var rebuilding = new TaskProjectionReconciler(_database.SecondProcessConnectionString, _authority).ReconcileAsync("team");
        Assert.False(rebuilding.IsCompleted);
        await transaction.CommitAsync();
        await rebuilding.WaitAsync(TimeSpan.FromSeconds(10));
        var task = (await _authority.GetAsync(SyncCallContext.Anonymous with { Scope = "team" }, "task"))!.Document;
        Assert.Equal("concurrent", task.Title);
        Assert.Equal("file", Assert.Single(task.Attachments).Id);
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
    }

    [Fact]
    public async Task FailureAfterFirstProjectionRollsBackWholeRepairAndCanRestart()
    {
        await Write("a", "first"); await Write("zfail", "last");
        await _database.ExecuteAsync("""
            CREATE TRIGGER bsync.FailRepair ON bsync.documents AFTER INSERT,UPDATE AS
            BEGIN
                IF EXISTS(SELECT 1 FROM inserted WHERE id_key=0x007a006600610069006c)
                    THROW 51000,'Injected repair failure',1;
            END
            """);
        var repair = new TaskProjectionReconciler(_database.ConnectionString, _authority);
        await Assert.ThrowsAsync<SqlException>(() => repair.ReconcileAsync("team"));
        var caller = SyncCallContext.Anonymous with { Scope = "team" };
        Assert.Null(await _authority.GetAsync(caller, "a"));
        Assert.Null(await _authority.GetAsync(caller, "zfail"));
        Assert.Equal(2, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
        await _database.ExecuteAsync("DROP TRIGGER bsync.FailRepair");
        Assert.Equal(2, (await repair.ReconcileAsync("team")).Written);
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.TaskProjectionOutbox"));
    }
}
