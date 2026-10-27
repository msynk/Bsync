using Bsync.Server.SqlServer.Workflows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class RelationalWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LedgerEnlistsInEfExecutionStrategyAcrossCommitAmbiguity(bool loseCommitReply)
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync(SqlServerCommandLedger.MigrationSql);
        await db.ExecuteAsync("CREATE TABLE dbo.Counter(Value int NOT NULL); INSERT dbo.Counter VALUES(0)");
        await using var context = new DbContext(new DbContextOptionsBuilder().UseSqlServer(db.ConnectionString).Options);
        var attempts = 0;
        var result = await new RetryStrategy(context).ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var connection = (SqlConnection)context.Database.GetDbConnection();
            var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();
            var value = await SqlServerCommandLedger.ExecuteAsync(connection, sqlTransaction, "scope", "command", "actor", "payload",
                DateTimeOffset.UtcNow.AddDays(1), async ct =>
                {
                    await using var update = new SqlCommand("UPDATE dbo.Counter SET Value=Value+1", connection, sqlTransaction);
                    await update.ExecuteNonQueryAsync(ct); return "committed";
                });
            if (++attempts == 1 && !loseCommitReply) throw new RetryableFailure();
            await transaction.CommitAsync();
            if (attempts == 1) throw new RetryableFailure();
            return value;
        });
        Assert.Equal(2, attempts); Assert.Equal(loseCommitReply, result.Replayed);
        Assert.Equal(1, await db.ScalarAsync("SELECT Value FROM dbo.Counter"));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncCommandLedger"));
    }

    [Fact]
    public async Task ConcurrentCommandRetriesCommitDomainWorkOnce()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync(SqlServerCommandLedger.MigrationSql);
        await db.ExecuteAsync("CREATE TABLE dbo.Counter(Value int NOT NULL); INSERT dbo.Counter VALUES(0)");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var connection = await db.OpenAsync(); await using var tx = connection.BeginTransaction();
            var result = await SqlServerCommandLedger.ExecuteAsync(connection, tx, "scope", "one", "actor", "payload", DateTimeOffset.UtcNow.AddDays(1), async ct =>
            {
                await using var update = new SqlCommand("UPDATE dbo.Counter SET Value=Value+1", connection, tx);
                await update.ExecuteNonQueryAsync(ct); return "done";
            });
            await tx.CommitAsync(); return result;
        }));
        Assert.Single(results, r => !r.Replayed);
        Assert.Equal(1, await db.ScalarAsync("SELECT Value FROM dbo.Counter"));
    }

    private sealed class RetryableFailure : Exception;
    private sealed class RetryStrategy(DbContext context) : ExecutionStrategy(context, 2, TimeSpan.Zero)
    { protected override bool ShouldRetryOn(Exception exception) => exception is RetryableFailure; }

    [Fact]
    public async Task UnknownWorkflowSchemaRefusesRuntimeAndMigrationWithoutDiscardingEvidence()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync(SqlServerCommandLedger.MigrationSql);
        await db.ExecuteAsync(SqlServerProjectionOutbox.MigrationSql);
        await db.ExecuteAsync(SqlServerChangeTrackingCapture.MigrationSql);
        var effects = new ExternalEffectOutbox(db.ConnectionString); await effects.InitializeAsync();
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        await db.ExecuteAsync("UPDATE dbo.BsyncWorkflowSchema SET Version=3");
        await using (var connection = await db.OpenAsync())
        await using (var tx = connection.BeginTransaction())
            await Assert.ThrowsAsync<NotSupportedException>(() => SqlServerCommandLedger.ExecuteAsync(connection, tx, "scope", "id", "actor", "payload", DateTimeOffset.UtcNow, _ => throw new InvalidOperationException("Must not execute.")));
        await Assert.ThrowsAsync<NotSupportedException>(() => new SqlServerProjectionOutbox(db.ConnectionString, "notes").DrainAsync((_, _, _, _) => Task.CompletedTask, (_, _, _, _, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<NotSupportedException>(() => effects.DrainAsync((_, _) => Task.FromResult(ExternalEffectOutcome.Succeeded), (_, _, _, _, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<NotSupportedException>(() => new SqlServerChangeTrackingCapture(db.ConnectionString, "notes", "epoch", "v1", [new("dbo", "Notes", "Id")]).StageAsync());
        await Assert.ThrowsAsync<SqlException>(() => effects.InitializeAsync());
        Assert.Equal(4, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncWorkflowSchema WHERE Version=3"));
    }
    [Fact]
    public async Task LedgerRollbackRetryBindingAndExpiredResultCannotReexecute()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync(SqlServerCommandLedger.MigrationSql);
        await db.ExecuteAsync("CREATE TABLE dbo.DomainCount(Value int NOT NULL); INSERT dbo.DomainCount VALUES(0);");
        async Task<CommandLedgerResult> Execute(string actor, string payload, bool commit)
        {
            await using var connection = await db.OpenAsync();
            await using var tx = connection.BeginTransaction();
            var result = await SqlServerCommandLedger.ExecuteAsync(connection, tx, "scope", "command", actor, payload,
                DateTimeOffset.UtcNow.AddHours(1), async ct =>
                {
                    await using var update = new SqlCommand("UPDATE dbo.DomainCount SET Value=Value+1", connection, tx);
                    await update.ExecuteNonQueryAsync(ct); return "done";
                });
            if (commit) await tx.CommitAsync();
            return result;
        }
        await Execute("alice", "payload", false);
        Assert.Equal(0, await db.ScalarAsync("SELECT Value FROM dbo.DomainCount"));
        Assert.False((await Execute("alice", "payload", true)).Replayed);
        Assert.True((await Execute("alice", "payload", true)).Replayed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("bob", "payload", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("alice", "changed", true));
        await db.ExecuteAsync("UPDATE dbo.BsyncCommandLedger SET ExpiresAt='2000-01-01'");
        Assert.True((await Execute("alice", "payload", true)).ResultExpired);
        Assert.Equal(1, await db.ScalarAsync("SELECT Value FROM dbo.DomainCount"));
    }

    [Fact]
    public async Task ProjectionCoalescesAndAcknowledgesOnlyWithPublication()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync(SqlServerProjectionOutbox.MigrationSql);
        await db.ExecuteAsync("CREATE TABLE dbo.Published(Value int NOT NULL); INSERT dbo.Published VALUES(0);");
        var outbox = new SqlServerProjectionOutbox(db.ConnectionString, "notes-v1");
        await using (var connection = await db.OpenAsync())
        await using (var tx = connection.BeginTransaction())
        {
            await outbox.RequestAsync(connection, tx, "scope", "one");
            await outbox.RequestAsync(connection, tx, "scope", "one");
            await tx.CommitAsync();
        }
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncProjectionOutbox"));
        Task Locks(SqlConnection _, SqlTransaction __, string ___, CancellationToken ____) => Task.CompletedTask;
        async Task Publish(SqlConnection connection, SqlTransaction tx, string scope, string id, CancellationToken ct)
        {
            await using var command = new SqlCommand("UPDATE dbo.Published SET Value=Value+1", connection, tx);
            await command.ExecuteNonQueryAsync(ct);
        }
        Assert.Equal(0, await outbox.DrainAsync(Locks, async (c,t,s,i,ct) => { await Publish(c,t,s,i,ct); throw new IOException("crash before acknowledgement"); }));
        Assert.Equal(0, await db.ScalarAsync("SELECT Value FROM dbo.Published"));
        await db.ExecuteAsync("UPDATE dbo.BsyncProjectionOutbox SET RetryAt=NULL");
        var results = await Task.WhenAll(outbox.DrainAsync(Locks, Publish), outbox.DrainAsync(Locks, Publish));
        Assert.Equal(1, results.Sum());
        Assert.Equal(1, await db.ScalarAsync("SELECT Value FROM dbo.Published"));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncProjectionOutbox"));
    }
}
