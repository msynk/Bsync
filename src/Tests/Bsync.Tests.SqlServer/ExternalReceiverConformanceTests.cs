using Bsync.Samples.Tasks.Server;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class ExternalReceiverConformanceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LostAcknowledgementRequiresDeduplicationLookupOrExplicitReconciliation(bool deduplicates, bool lookup)
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var outbox = new ExternalEffectOutbox(database.ConnectionString); await outbox.InitializeAsync();
        await using (var connection = await database.OpenAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await ExternalEffectOutbox.EnqueueAsync(connection, transaction, "authorized-scope", "command", "immutable payload");
            await transaction.CommitAsync();
        }
        var receiver = new Receiver(deduplicates, lookup);
        var outcomes = new List<ExternalEffectOutcome>();
        await outbox.DrainAsync(receiver, (_, _, _, outcome, _) => { outcomes.Add(outcome); return Task.CompletedTask; });
        await database.ExecuteAsync("UPDATE dbo.ExternalEffectOutbox SET RetryAt=NULL");
        await outbox.DrainAsync(receiver, (_, _, _, outcome, _) => { outcomes.Add(outcome); return Task.CompletedTask; });
        Assert.Equal(1, receiver.Effects);
        Assert.Equal(deduplicates ? 2 : 1, receiver.Sends);
        Assert.Equal(deduplicates || lookup ? ExternalEffectOutcome.Succeeded : ExternalEffectOutcome.ReconciliationRequired, Assert.Single(outcomes));
    }

    private sealed class Receiver(bool deduplicates, bool lookup) : IExternalEffectReceiver
    {
        public ExternalReceiverCapabilities Capabilities { get; } = new(deduplicates, lookup);
        public int Sends { get; private set; }
        public int Effects { get; private set; }
        public Task<bool> AuthorizeAsync(ExternalEffect effect, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ExternalEffectStatus> LookupAsync(ExternalEffect effect, CancellationToken cancellationToken) => Task.FromResult(Effects > 0 ? ExternalEffectStatus.Succeeded : ExternalEffectStatus.NotExecuted);
        public Task<ExternalEffectOutcome> SendAsync(ExternalEffect effect, CancellationToken cancellationToken)
        {
            Sends++;
            if (!deduplicates || Effects == 0) Effects++;
            if (Sends == 1) throw new IOException("Receiver committed; acknowledgement lost.");
            return Task.FromResult(ExternalEffectOutcome.Succeeded);
        }
    }
}
