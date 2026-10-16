using Bsync.Samples.Tasks.Server;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class ExternalEffectOutboxTests : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;
    private ExternalEffectOutbox _outbox = null!;
    public async Task InitializeAsync()
    {
        _database = await SqlServerDatabase.CreateAsync();
        _outbox = new(_database.ConnectionString);
        await _outbox.InitializeAsync();
        await _database.ExecuteAsync("CREATE TABLE dbo.EffectResults(Id nvarchar(256) NOT NULL, State int NOT NULL)");
    }
    public async Task DisposeAsync() => await _database.DisposeAsync();
    private async Task Enqueue(string id = "command", string payload = "{}", bool commit = true)
    {
        await using var connection = await _database.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await ExternalEffectOutbox.EnqueueAsync(connection, transaction, "scope", id, payload);
        if (commit) await transaction.CommitAsync();
    }
    private static async Task Publish(SqlConnection connection, SqlTransaction transaction, ExternalEffect effect, ExternalEffectOutcome outcome, CancellationToken ct)
    {
        await using var command = new SqlCommand("INSERT dbo.EffectResults VALUES(@id,@state)", connection, transaction);
        command.Parameters.AddWithValue("@id", effect.CommandId); command.Parameters.AddWithValue("@state", (int)outcome);
        await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task EnqueueRollsBackAndIdentityRemainsImmutableAfterCompletion()
    {
        await Enqueue(commit: false);
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.ExternalEffectOutbox"));
        await Enqueue(); await Enqueue();
        Assert.Equal(1, await _outbox.DrainAsync((_, _) => Task.FromResult(ExternalEffectOutcome.Succeeded), Publish));
        await Enqueue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Enqueue(payload: "changed"));
        var resent = 0;
        Assert.Equal(0, await _outbox.DrainAsync((_, _) => { resent++; return Task.FromResult(ExternalEffectOutcome.Succeeded); }, Publish));
        Assert.Equal(0, resent);
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.EffectResults"));
    }

    [Fact]
    public async Task LostReceiverReplyRepeatsStableKeyAndResultFailureRollsBackAcknowledgement()
    {
        await Enqueue();
        var effects = new HashSet<string>();
        var sends = 0;
        Task<ExternalEffectOutcome> Send(ExternalEffect effect, CancellationToken _)
        {
            effects.Add(effect.IdempotencyKey); sends++;
            if (sends == 1) throw new IOException("Receiver committed but reply was lost.");
            return Task.FromResult(ExternalEffectOutcome.Succeeded);
        }
        Assert.Equal(0, await _outbox.DrainAsync(Send, Publish));
        await _database.ExecuteAsync("UPDATE dbo.ExternalEffectOutbox SET RetryAt=NULL");
        Assert.Equal(0, await _outbox.DrainAsync(Send, async (c, t, e, o, ct) => { await Publish(c, t, e, o, ct); throw new IOException("Before result commit."); }));
        Assert.Equal(0, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.EffectResults"));
        await _database.ExecuteAsync("UPDATE dbo.ExternalEffectOutbox SET RetryAt=NULL");
        Assert.Equal(1, await _outbox.DrainAsync(Send, Publish));
        Assert.Equal(3, sends); Assert.Single(effects);
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.EffectResults"));
    }

    [Fact]
    public async Task ExpiredDispatcherCannotPublishOrAcknowledgeSuccessorResult()
    {
        await Enqueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? firstKey = null;
        var first = _outbox.DrainAsync(async (effect, _) =>
        {
            firstKey = effect.IdempotencyKey; entered.SetResult(); await release.Task;
            return ExternalEffectOutcome.Succeeded;
        }, Publish);
        await entered.Task;
        try
        {
            var stolen = 0;
            Assert.Equal(0, await new ExternalEffectOutbox(_database.ConnectionString).DrainAsync((_, _) => { stolen++; return Task.FromResult(ExternalEffectOutcome.Succeeded); }, Publish));
            Assert.Equal(0, stolen);
            await _database.ExecuteAsync("UPDATE dbo.ExternalEffectOutbox SET LeaseUntil='2000-01-01'");
            Assert.Equal(1, await new ExternalEffectOutbox(_database.ConnectionString).DrainAsync((effect, _) =>
            {
                Assert.Equal(firstKey, effect.IdempotencyKey);
                return Task.FromResult(ExternalEffectOutcome.Rejected);
            }, Publish));
        }
        finally { release.SetResult(); }
        Assert.Equal(0, await first);
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.EffectResults"));
        Assert.Equal((int)ExternalEffectOutcome.Rejected, await _database.ScalarAsync("SELECT State FROM dbo.EffectResults"));
    }

    [Fact]
    public async Task PoisonRequestsDoNotBlockOtherWorkAndCaseDistinctIdsHaveDifferentKeys()
    {
        await Enqueue("a"); await Enqueue("A");
        var keys = new HashSet<string>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await _outbox.DrainAsync((effect, _) =>
            {
                keys.Add(effect.IdempotencyKey);
                return Task.FromResult(effect.CommandId == "a" ? ExternalEffectOutcome.RetryLater : ExternalEffectOutcome.Succeeded);
            }, Publish);
            await _database.ExecuteAsync("UPDATE dbo.ExternalEffectOutbox SET RetryAt=NULL");
        }
        Assert.Equal(2, keys.Count);
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.ExternalEffectOutbox WHERE State='quarantined'"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT COUNT(*) FROM dbo.EffectResults"));
        var resent = 0;
        Assert.Equal(0, await _outbox.DrainAsync((_, _) => { resent++; return Task.FromResult(ExternalEffectOutcome.Succeeded); }, Publish));
        Assert.Equal(0, resent);
    }
}
