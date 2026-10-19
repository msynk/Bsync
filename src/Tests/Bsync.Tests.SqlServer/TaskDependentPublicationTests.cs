using System.Security.Claims;
using Bsync.Protocol;
using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Server;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>Real caller-owned transactions and competing connections, including the same recipe invoked by POST /api/tasks.</summary>
public sealed class TaskDependentPublicationTests : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;
    private SqlServerSyncAuthority<TaskDocument> _tasks = null!;
    private SqlServerSyncAuthority<AudienceTask> _audience = null!;
    private TaskAudienceProjection _projection = null!;
    private TaskAudiencePublication _acl = null!;
    private GateHandler _handler = null!;
    private int _hints;
    private static SyncCallContext Caller(string principal = "alice") => new(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim("sub", principal), new Claim(ClaimTypes.NameIdentifier, principal)], "test")), "team");

    public async Task InitializeAsync()
    {
        _database = await SqlServerDatabase.CreateAsync();
        await TasksDb.EnsureTablesAsync(_database.ConnectionString);
        _audience = await TaskAudiencePublication.CreateAuthorityAsync(_database.ConnectionString);
        _projection = new(_audience);
        _handler = new(new TaskWriteHandler { AudienceProjection = _projection });
        _tasks = await SqlServerSyncAuthority<TaskDocument>.CreateAsync(new()
        {
            ConnectionString = _database.ConnectionString, Collection = "tasks", DocumentType = TasksJson.Default.TaskDocument,
            WriteHandler = _handler,
        });
        _tasks.Committed += _projection.NotifyCommitted;
        _audience.Committed += _ => Interlocked.Increment(ref _hints);
        _acl = new(_database.ConnectionString, _tasks, _audience);
        await _acl.InitializeAsync();
        // A grant can precede the task's creation; only publication of an actual domain row makes content readable.
        await _acl.SetReadersAsync("team", "task", ["alice"]);
        _hints = 0;
    }

    public async Task DisposeAsync()
    {
        _tasks.Committed -= _projection.NotifyCommitted;
        await _tasks.DisposeAsync(); await _audience.DisposeAsync(); await _database.DisposeAsync();
    }

    private TaskApiPublication Api() => new(_database.ConnectionString, _tasks) { AudienceProjection = _projection };
    private async Task<PushOutcome<TaskDocument>> Push(string operation, long? version, string title, bool deleted = false,
        CancellationToken ct = default) => (await _tasks.PushAsync(Caller(), new([new(operation, "task", version,
            new TaskDocument { Id = "task", Title = title, Deleted = deleted })]), ct)).Outcomes.Single();

    [Theory]
    [InlineData("before-commit")]
    [InlineData("after-commit")]
    public async Task RetryUsesFreshTransactionAndCommitsBothFeedsOnce(string failureBoundary)
    {
        var failures = 0; var retries = 0;
        var recipe = new TaskApiPublication(_database.ConnectionString, _tasks)
        {
            AudienceProjection = _projection,
            StrategyFactory = db => new TestStrategy(db, () => retries++),
            Boundary = boundary =>
            {
                if (boundary == failureBoundary && failures++ == 0) throw new RetryableFailure();
                return Task.CompletedTask;
            },
        };
        await recipe.CreateAsync("team", "alice", new("task", "Created"));
        Assert.Equal(1, retries);
        Assert.Equal(1, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks WHERE Id='task'"));
        Assert.Equal(1, await _tasks.GetHighestVersionAsync("team"));
        Assert.Equal(1, await _audience.GetHighestVersionAsync("team"));
        Assert.Equal("Created", (await _audience.GetAsync(Caller(), "task"))!.Document.Title);
        Assert.Null(await _audience.GetAsync(Caller("bob"), "task"));
        Assert.Equal(1, _hints);
        await Api().CreateAsync("team", "alice", new("task", "Created"));
        Assert.Equal(1, await _audience.GetHighestVersionAsync("team"));
    }

    [Fact]
    public async Task PermanentFailureRollsBackBothFeedsAndSendsNoHint()
    {
        var recipe = new TaskApiPublication(_database.ConnectionString, _tasks)
        { AudienceProjection = _projection, Boundary = _ => throw new InvalidDataException("before commit") };
        await Assert.ThrowsAsync<InvalidDataException>(() => recipe.CreateAsync("team", "alice", new("task", "Aborted")));
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.Tasks"));
        Assert.Null(await _tasks.GetAsync(Caller(), "task"));
        Assert.Null(await _audience.GetAsync(Caller(), "task"));
        Assert.Equal(0, _hints);
    }

    [Fact]
    public async Task SyncMutationAndDeletionPublishDependentFeedAndReplayDoesNotAdvanceIt()
    {
        await Api().CreateAsync("team", "alice", new("task", "Created"));
        var update = await Push("update", 1, "Edited");
        Assert.Equal(PushOutcomeKind.Accepted, update.Kind);
        var visible = (await _audience.GetAsync(Caller(), "task"))!;
        Assert.Equal(("Edited", 2L), (visible.Document.Title, visible.Document.Revision));
        Assert.True((await Push("update", 1, "Edited")).IsDuplicate);
        Assert.Equal(visible.Version, (await _audience.GetAsync(Caller(), "task"))!.Version);
        var checkpoint = (await _audience.PullAsync(Caller(), new(Checkpoint.Start, 10))).Checkpoint;
        Assert.Equal(PushOutcomeKind.Accepted, (await Push("delete", update.Version, "Edited", deleted: true)).Kind);
        var deleted = (await _audience.PullAsync(Caller(), new(checkpoint, 10))).Changes.Single();
        Assert.True(deleted.Document.Deleted);
        Assert.True((await _tasks.GetAsync(Caller(), "task"))!.Document.Deleted);
        Assert.Equal(3, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks WHERE Deleted=1"));
        await Assert.ThrowsAsync<TaskCreationConflictException>(() => Api().CreateAsync("team", "alice", new("task", "Created")));
    }

    [Fact]
    public async Task CallerRollbackPreservesDomainBothFeedsAndReceipts()
    {
        await Api().CreateAsync("team", "alice", new("task", "Created"));
        await using (var connection = await _database.OpenAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            var result = await _tasks.PushAsync(Caller(), new([new("aborted", "task", 1,
                new TaskDocument { Id = "task", Title = "Uncommitted" })]), transaction);
            Assert.Equal(PushOutcomeKind.Accepted, result.Outcomes.Single().Kind);
            Assert.Same(connection, transaction.Connection); // no hidden ownership/commit
            Assert.Equal(1, _hints); // only the initial API creation
            await transaction.RollbackAsync();
        }
        Assert.Equal("Created", (await _tasks.GetAsync(Caller(), "task"))!.Document.Title);
        Assert.Equal("Created", (await _audience.GetAsync(Caller(), "task"))!.Document.Title);
        Assert.Equal(1, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks"));
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM bsync.receipts"));
    }

    [Fact]
    public async Task ApiHoldingTransactionSerializesSyncAndAclRepair()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recipe = new TaskApiPublication(_database.ConnectionString, _tasks)
        {
            AudienceProjection = _projection,
            Boundary = async boundary =>
            {
                if (boundary == "before-commit") { entered.SetResult(); await release.Task.WaitAsync(timeout.Token); }
            },
        };
        var creating = recipe.CreateAsync("team", "alice", new("task", "API wins"), timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var syncing = Push("competing", null, "Sync loses", ct: timeout.Token);
        var granting = _acl.SetReadersAsync("team", "task", ["bob"], timeout.Token);
        var repairing = _acl.ReconcileAsync("team", cancellationToken: timeout.Token);
        try
        {
            Assert.False(syncing.IsCompleted); Assert.False(granting.IsCompleted); Assert.False(repairing.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(creating, syncing, granting, repairing);
        Assert.Equal(PushOutcomeKind.Conflict, (await syncing).Kind);
        Assert.Null(await _audience.GetAsync(Caller(), "task"));
        Assert.Equal("API wins", (await _audience.GetAsync(Caller("bob"), "task"))!.Document.Title);
        Assert.Equal(1, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks"));
    }

    [Fact]
    public async Task SyncHoldingFeedSerializesApiAndRelatedReaders()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Before = async () => { entered.SetResult(); await release.Task.WaitAsync(timeout.Token); };
        var syncing = Push("sync-first", null, "Sync wins", ct: timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var creating = Api().CreateAsync("team", "alice", new("task", "API loses"), timeout.Token);
        var granting = _acl.SetReadersAsync("team", "task", ["bob"], timeout.Token);
        try { Assert.False(creating.IsCompleted); Assert.False(granting.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.Equal(PushOutcomeKind.Accepted, (await syncing).Kind);
        await Assert.ThrowsAsync<TaskCreationConflictException>(() => creating);
        await granting;
        Assert.Equal("Sync wins", (await _tasks.GetAsync(Caller(), "task"))!.Document.Title);
        Assert.Equal("Sync wins", (await _audience.GetAsync(Caller("bob"), "task"))!.Document.Title);
        Assert.Null(await _audience.GetAsync(Caller(), "task"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT Revision FROM dbo.Tasks"));
    }

    [Fact]
    public async Task IntentPublishesDependencyInLedgerTransactionAndReplayDoesNotExecuteAgain()
    {
        await Api().CreateAsync("team", "alice", new("task", "Created"));
        await using var intents = await SqlServerSyncAuthority<TaskIntent>.CreateAsync(new()
        {
            ConnectionString = _database.ConnectionString, Collection = "intents", DocumentType = TasksJson.Default.TaskIntent,
            WriteHandler = new IntentWriteHandler(_tasks) { AudienceProjection = _projection },
        });
        var createdAt = DateTimeOffset.UtcNow; // Retrying an immutable request must preserve its original timestamp.
        PushRequest<TaskIntent> Request() => new([new("intent-op", "intent", null,
            new TaskIntent { Id = "intent", Kind = IntentKinds.Rename, TaskId = "task", Title = "Renamed", TaskRevision = 1, CreatedAt = createdAt })]);
        await using (var connection = await _database.OpenAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            Assert.Equal(IntentStates.Executed, (await intents.PushAsync(Caller(), Request(), transaction)).Outcomes.Single().Document!.State);
            await transaction.RollbackAsync();
        }
        Assert.Equal("Created", (await _audience.GetAsync(Caller(), "task"))!.Document.Title);
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.IntentExecutions"));
        var first = await intents.PushAsync(Caller(), Request());
        Assert.Equal(IntentStates.Executed, first.Outcomes.Single().Document!.State);
        var visible = (await _audience.GetAsync(Caller(), "task"))!;
        Assert.Equal(("Renamed", 2L), (visible.Document.Title, visible.Document.Revision));
        Assert.True((await intents.PushAsync(Caller(), Request())).Outcomes.Single().IsDuplicate);
        Assert.Equal(visible.Version, (await _audience.GetAsync(Caller(), "task"))!.Version);
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.IntentExecutions"));
    }

    [Fact]
    public async Task HelperRefusesMismatchedTransactionWithoutClosingCallerConnections()
    {
        await using var connection = await _database.OpenAsync();
        await using var other = await _database.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await Assert.ThrowsAsync<ArgumentException>(() => _projection.PublishAsync(other, transaction, "team", "task"));
        Assert.Same(connection, transaction.Connection);
        Assert.Equal(System.Data.ConnectionState.Open, other.State);
        await transaction.RollbackAsync();
    }

    private sealed class GateHandler(TaskWriteHandler inner) : ISyncWriteHandler<TaskDocument>
    {
        public Func<Task>? Before { get; set; }
        public async ValueTask<SyncWriteDecision<TaskDocument>> HandleAsync(SyncWriteContext<TaskDocument> write, CancellationToken ct)
        {
            if (Before is not null) await Before();
            return await inner.HandleAsync(write, ct);
        }
    }
    private sealed class RetryableFailure : Exception;
    private sealed class TestStrategy(DbContext context, Action retried) : ExecutionStrategy(context, 2, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception)
        { if (exception is not RetryableFailure) return false; retried(); return true; }
    }
}
