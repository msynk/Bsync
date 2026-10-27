using Bsync.Server.SqlServer.Workflows;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class CaptureGenerationTests
{
    [Fact]
    public async Task PagedGenerationResumesRollbacksCatchesDeletesAndHandsOffConcurrentWriters()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET CHANGE_TRACKING=ON (CHANGE_RETENTION=2 DAYS,AUTO_CLEANUP=ON)");
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Source(Id int NOT NULL PRIMARY KEY, Visible bit NOT NULL, Value int NOT NULL);
            ALTER TABLE dbo.Source ENABLE CHANGE_TRACKING;
            INSERT dbo.Source VALUES(1,1,10),(2,1,20),(3,1,30),(4,1,40),(5,1,50);
            CREATE TABLE dbo.Candidate(Generation uniqueidentifier NOT NULL, Id int NOT NULL, Value int NOT NULL, PRIMARY KEY(Generation,Id));
            CREATE TABLE dbo.Active(Id int NOT NULL PRIMARY KEY, Value int NOT NULL);
            INSERT dbo.Active VALUES(99,999);
            """);
        await db.ExecuteAsync(SqlServerChangeTrackingCapture.MigrationSql);
        SqlServerChangeTrackingCapture Capture() => new(db.ConnectionString, "notes", "epoch", "schema", [new("dbo", "Source", "Id")], maximumKeys: 2);
        var capture = Capture();
        var generation = await capture.BeginGenerationAsync(maximumKeys: 20);
        Assert.Equal(5, generation.TotalKeys);
        Assert.Equal(generation.Id, (await Capture().BeginGenerationAsync()).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StageAsync());
        async Task Build(SqlConnection c, SqlTransaction t, Guid id, IReadOnlyList<ChangeTrackingSourceKey> keys, CancellationToken ct)
        {
            foreach (var key in keys)
            {
                await using var command = new SqlCommand("""
                    DELETE dbo.Candidate WHERE Generation=@generation AND Id=@key;
                    INSERT dbo.Candidate SELECT @generation,Id,Value FROM dbo.Source WHERE Id=@key AND Visible=1
                    """, c, t);
                command.Parameters.AddWithValue("@generation", id); command.Parameters.AddWithValue("@key", int.Parse(key.Key));
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        Task Locks(SqlConnection _, SqlTransaction __, CancellationToken ___) => Task.CompletedTask;
        async Task Activate(SqlConnection c, SqlTransaction t, Guid id, CancellationToken ct)
        {
            await using var command = new SqlCommand("DELETE dbo.Active; INSERT dbo.Active SELECT Id,Value FROM dbo.Candidate WHERE Generation=@id", c, t);
            command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync(ct);
        }
        await Assert.ThrowsAsync<IOException>(() => capture.BuildGenerationPageAsync(generation.Id, async (c, t, id, keys, ct) =>
        { await Build(c, t, id, keys, ct); throw new IOException("private diagnostic must not be stored"); }, 2));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.Candidate"));
        Assert.Equal("IOException", (await capture.GetGenerationAsync())!.Failure);
        generation = await Capture().BuildGenerationPageAsync(generation.Id, Build, 2);
        Assert.Null(generation.Failure); Assert.Equal(2, generation.CompletedKeys);
        await db.ExecuteAsync("DELETE dbo.Source WHERE Id=1; UPDATE dbo.Source SET Visible=0 WHERE Id=2; UPDATE dbo.Source SET Value=35 WHERE Id=3; INSERT dbo.Source VALUES(6,1,60)");
        while (generation.CompletedKeys < generation.TotalKeys) generation = await Capture().BuildGenerationPageAsync(generation.Id, Build, 2);
        Assert.Equal(999, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Active"));
        // Four changes exceed the two-key final handoff budget; failed activation cannot change the old scope.
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.ActivateGenerationAsync(generation.Id, Locks, Build, Activate));
        Assert.Equal(999, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Active"));
        generation = await capture.CatchUpGenerationAsync(generation.Id);
        while (generation.CompletedKeys < generation.TotalKeys) generation = await Capture().BuildGenerationPageAsync(generation.Id, Build, 2);
        await db.ExecuteAsync("UPDATE dbo.Source SET Value=61 WHERE Id=6");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = capture.ActivateGenerationAsync(generation.Id, Locks, Build, async (c, t, id, ct) =>
        { await Activate(c, t, id, ct); entered.SetResult(); await release.Task.WaitAsync(ct); });
        Task? writer = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            writer = db.ExecuteAsync("INSERT dbo.Source VALUES(7,1,70)");
            Assert.False(writer.IsCompleted);
        }
        finally { release.TrySetResult(); Assert.True(await activation); }
        await writer!;
        Assert.Equal(186, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Active"));
        Assert.False(await capture.ActivateGenerationAsync(generation.Id, Locks, Build, (_, _, _, _) => throw new Exception("Activated twice")));
        Assert.Null(await capture.GetGenerationAsync());
        Assert.Equal("7", Assert.Single((await capture.StageAsync()).Keys).Key);
        await capture.PublishAsync((_, _, _, _) => Task.CompletedTask);
        var replacement = await capture.BeginGenerationAsync("epoch");
        await capture.AbandonGenerationAsync(replacement.Id);
        Assert.Null(await capture.GetGenerationAsync());
        Assert.Equal(186, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Active"));
    }
}
