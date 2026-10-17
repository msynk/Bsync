using System.Security.Claims;
using Bsync.Protocol;
using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Server;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class TaskAudiencePublicationTests : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;
    private SqlServerSyncAuthority<TaskDocument> _tasks = null!;
    private SqlServerSyncAuthority<AudienceTask> _audience = null!;
    private TaskAudiencePublication _recipe = null!;
    public async Task InitializeAsync()
    {
        _database = await SqlServerDatabase.CreateAsync();
        await TasksDb.EnsureTablesAsync(_database.ConnectionString);
        _tasks = await SqlServerSyncAuthority<TaskDocument>.CreateAsync(new() { ConnectionString = _database.ConnectionString, DocumentType = TasksJson.Default.TaskDocument, Collection = "tasks" });
        _audience = await TaskAudiencePublication.CreateAuthorityAsync(_database.ConnectionString);
        _recipe = new(_database.ConnectionString, _tasks, _audience); await _recipe.InitializeAsync();
        await new TaskApiPublication(_database.ConnectionString, _tasks).CreateAsync("team", "admin", new("task", "Private task"));
    }
    public async Task DisposeAsync() { await _audience.DisposeAsync(); await _tasks.DisposeAsync(); await _database.DisposeAsync(); }
    private static SyncCallContext Caller(string principal) => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, principal)], "test")), "team");

    [Fact]
    public async Task RelatedAclMutationPublishesRemovalAndGrantWithoutTrustingSubmittedReaders()
    {
        await _recipe.SetReadersAsync("team", "task", ["alice"]);
        var initial = await _audience.PullAsync(Caller("alice"), new(Checkpoint.Start, 10) { Features = [SyncFeatures.Removals] });
        Assert.Single(initial.Changes);
        Assert.Null(await _audience.GetAsync(Caller("bob"), "task"));
        await _recipe.SetReadersAsync("team", "task", ["bob"]);
        var removed = await _audience.PullAsync(Caller("alice"), new(initial.Checkpoint, 10) { Features = [SyncFeatures.Removals] });
        Assert.Contains("task", removed.Removals!);
        Assert.NotNull(await _audience.GetAsync(Caller("bob"), "task"));
        var forged = await _audience.PushAsync(Caller("mallory"), new([new("forge", "task", null, new AudienceTask { Id = "task", Readers = ["mallory"] })]));
        Assert.Null(await _audience.GetAsync(Caller("mallory"), "task"));
        Assert.Equal(PushOutcomeKind.Rejected, forged.Outcomes.Single().Kind);
    }

    [Fact]
    public async Task AclRollbackAndConcurrentRepairCannotExposeUncommittedGrants()
    {
        await _recipe.SetReadersAsync("team", "task", ["alice"]);
        var original = (await _audience.GetAsync(Caller("alice"), "task"))!.Version;
        await using (var connection = new SqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();
            await _recipe.AcquirePublicationLocksAsync("team", transaction);
            await using var mutation = new SqlCommand("DELETE dbo.TaskReaders WHERE Tenant='team' AND TaskId='task'", connection, transaction);
            await mutation.ExecuteNonQueryAsync();
            await _recipe.PublishAsync(connection, transaction, "team", "task");
            await transaction.RollbackAsync();
        }
        Assert.Equal(original, (await _audience.GetAsync(Caller("alice"), "task"))!.Version);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var changes = Enumerable.Range(0, 12).Select(i => i % 3 == 0
            ? _recipe.ReconcileAsync("team", cancellationToken: timeout.Token)
            : _recipe.SetReadersAsync("team", "task", [i % 2 == 0 ? "alice" : "bob"], timeout.Token));
        await Task.WhenAll(changes);
        await _recipe.SetReadersAsync("team", "task", ["bob"]);
        await _recipe.ReconcileAsync("team");
        Assert.Null(await _audience.GetAsync(Caller("alice"), "task"));
        Assert.NotNull(await _audience.GetAsync(Caller("bob"), "task"));
    }

    [Fact]
    public async Task FullRepairReconcilesHardDeletionAndMembershipAndRefusesPartialSnapshots()
    {
        await _recipe.SetReadersAsync("team", "task", ["alice"]);
        var before = (await _audience.GetAsync(Caller("alice"), "task"))!.Version;
        await new TaskApiPublication(_database.ConnectionString, _tasks).CreateAsync("team", "admin", new("second", "Second"));
        await _recipe.SetReadersAsync("team", "second", ["alice"]);
        var checkpoint = (await _audience.PullAsync(Caller("alice"), new(Checkpoint.Start, 10) { Features = [SyncFeatures.Removals] })).Checkpoint;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _recipe.ReconcileAsync("team", maximumTasks: 1));
        Assert.Equal(before, (await _audience.GetAsync(Caller("alice"), "task"))!.Version);
        // Offline administrative repair input: no concurrent writers during these deliberately unpublished mutations.
        await _database.ExecuteAsync("DELETE dbo.Tasks WHERE Id='second'; DELETE dbo.TaskReaders WHERE TaskId='task'");
        await _recipe.ReconcileAsync("team");
        Assert.Null(await _audience.GetAsync(Caller("alice"), "task"));
        var repaired = await _audience.PullAsync(Caller("alice"), new(checkpoint, 10) { Features = [SyncFeatures.Removals] });
        Assert.Contains("task", repaired.Removals!);
        Assert.Equal("second", Assert.Single(repaired.Changes).Document.Id);
        Assert.True(repaired.Changes.Single().Document.Deleted);
    }
}
