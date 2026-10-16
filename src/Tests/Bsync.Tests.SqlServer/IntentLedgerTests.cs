using System.Security.Claims;
using System.Text.Json;
using Bsync.Protocol;
using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Server;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class IntentLedgerTests : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;
    private SqlServerSyncAuthority<TaskDocument> _tasks = null!;
    public async Task InitializeAsync()
    {
        _database = await SqlServerDatabase.CreateAsync();
        await TasksDb.EnsureTablesAsync(_database.ConnectionString);
        _tasks = await SqlServerSyncAuthority<TaskDocument>.CreateAsync(new()
        {
            ConnectionString = _database.ConnectionString, Collection = "tasks", DocumentType = TasksJson.Default.TaskDocument,
        });
        await _database.ExecuteAsync("INSERT dbo.Tasks(Tenant,Id,Title,Slug,Done,Deleted,Revision) VALUES('team','task','title','title',0,0,1)");
    }
    public async Task DisposeAsync() { await _tasks.DisposeAsync(); await _database.DisposeAsync(); }
    private async Task<SyncWriteDecision<TaskIntent>> Execute(TaskIntent request, bool commit = true, string actor = "alice")
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, TasksJson.Default.TaskIntent), TasksJson.Default.TaskIntent)!;
        await using var connection = await _database.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var caller = new SyncCallContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", actor)], "test")), "team");
        // New transport operation and absent result document simulate independent receipt/result retention.
        var context = new SyncWriteContext<TaskIntent>(caller, new(Guid.NewGuid().ToString("N"), request.Id, null, copy), null, connection, transaction);
        var decision = await new IntentWriteHandler(_tasks).HandleAsync(context, default);
        if (commit && decision.Kind == SyncWriteDecisionKind.Accept) await transaction.CommitAsync(); else await transaction.RollbackAsync();
        return decision;
    }

    [Fact]
    public async Task NewTransportOperationReplaysCommandResultAndExpiredResultNeverReexecutes()
    {
        var request = new TaskIntent { Id = "command", Kind = IntentKinds.Complete, TaskId = "task", TaskRevision = 1, State = IntentStates.Executed, ExecutedBy = "forged" };
        var first = await Execute(request);
        Assert.Equal("alice", first.Document!.ExecutedBy);
        var replay = await Execute(request);
        Assert.Equal(IntentStates.Executed, replay.Document!.State);
        Assert.Equal(2, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks WHERE Id='task'"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.IntentExecutions"));
        Assert.Equal(IntentCodes.Immutable, (await Execute(request, actor: "other")).ErrorCode);
        await _database.ExecuteAsync("UPDATE dbo.IntentExecutions SET Result=NULL,ResultExpiresAt='2000-01-01'");
        Assert.Equal(IntentCodes.ResultExpired, (await Execute(request)).ErrorCode);
        Assert.Equal(2, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks WHERE Id='task'"));
    }

    [Fact]
    public async Task UnsupportedOrOversizedRequestsNeverExecuteAndBusinessRefusalsRemainDistinct()
    {
        var request = new TaskIntent { Id = "invalid", Kind = IntentKinds.Rename, TaskId = "task", Title = new string('x', 65536) };
        Assert.Equal(IntentCodes.InvalidRequest, (await Execute(request)).ErrorCode);
        request.RequestSchema = 2;
        Assert.Equal(IntentCodes.UnsupportedSchema, (await Execute(request)).ErrorCode);
        request.RequestSchema = 1; request.Title = "valid"; request.TaskRevision = 99;
        var refusal = await Execute(request);
        Assert.Equal(SyncWriteDecisionKind.Accept, refusal.Kind);
        Assert.Equal(IntentStates.Rejected, refusal.Document!.State);
        Assert.Equal(IntentCodes.TargetChanged, refusal.Document.Code);
        Assert.Equal(1, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks WHERE Id='task'"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.IntentExecutions"));
    }

    [Fact]
    public async Task RollbackHasNeitherEffectNorLedgerAndConcurrentRetriesApplyOnce()
    {
        var request = new TaskIntent { Id = "command", Kind = IntentKinds.Rename, TaskId = "task", Title = "changed" };
        await Execute(request, commit: false);
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.IntentExecutions"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks WHERE Id='task'"));
        var decisions = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Execute(request)));
        Assert.All(decisions, d => Assert.Equal(IntentStates.Executed, d.Document!.State));
        Assert.Equal(2, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks WHERE Id='task'"));
        request.Title = "mutation";
        Assert.Equal(IntentCodes.Immutable, (await Execute(request)).ErrorCode);
    }
}
