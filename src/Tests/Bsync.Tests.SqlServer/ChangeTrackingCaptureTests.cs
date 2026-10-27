using Bsync.Server.SqlServer.Workflows;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class ChangeTrackingCaptureTests
{
    [Fact]
    public async Task CompetingWorkerCannotAdvanceAnOwnedBatchAndExpiredBoundaryFailsClosed()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET CHANGE_TRACKING=ON (CHANGE_RETENTION=2 DAYS,AUTO_CLEANUP=ON)");
        await db.ExecuteAsync("CREATE TABLE dbo.Source(Id int NOT NULL PRIMARY KEY); ALTER TABLE dbo.Source ENABLE CHANGE_TRACKING; INSERT dbo.Source VALUES(1)");
        await db.ExecuteAsync(SqlServerChangeTrackingCapture.MigrationSql);
        var first = new SqlServerChangeTrackingCapture(db.ConnectionString, "notes", "epoch", "v1", [new("dbo", "Source", "Id")]);
        var second = new SqlServerChangeTrackingCapture(db.SecondProcessConnectionString, "notes", "epoch", "v1", [new("dbo", "Source", "Id")]);
        var batch = await first.StageAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishing = first.PublishAsync(async (_, _, _, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<IOException>(() => second.StageAsync());
            var health = await second.GetHealthAsync();
            Assert.Equal(batch.Id, health.PendingBatchId); Assert.Null(health.PublishedVersion);
        }
        finally { release.TrySetResult(); await publishing; }
        Assert.False(await second.PublishAsync((_, _, _, _) => throw new InvalidOperationException("Already published batch replayed.")));
        // Fault-inject a cursor older than SQL's real minimum-valid version. This tests the guard, not natural cleanup timing.
        await db.ExecuteAsync("UPDATE dbo.BsyncCaptureState SET Boundary=CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.Source'))-1");
        Assert.Equal("retention-expired", (await first.GetHealthAsync()).Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.StageAsync());
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncCaptureBatches"));
        await first.BeginResnapshotAsync("epoch");
        Assert.True((await first.StageAsync()).InitialSnapshot);
    }

    [LocalDbRestoreFact]
    public async Task RestoredDatabaseRequiresExternalEpochAndFullReplacement()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        var directory = Directory.CreateTempSubdirectory("bsync-capture-restore-").FullName;
        var backup = Path.Combine(directory, "source.bak");
        try
        {
            await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
            await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET CHANGE_TRACKING=ON (CHANGE_RETENTION=2 DAYS,AUTO_CLEANUP=ON)");
            await db.ExecuteAsync("CREATE TABLE dbo.Source(Id int NOT NULL PRIMARY KEY, Value int NOT NULL); ALTER TABLE dbo.Source ENABLE CHANGE_TRACKING; INSERT dbo.Source VALUES(1,10); CREATE TABLE dbo.Projection(Id int NOT NULL PRIMARY KEY, Value int NOT NULL);");
            await db.ExecuteAsync(SqlServerChangeTrackingCapture.MigrationSql);
            SqlServerChangeTrackingCapture Capture(string epoch) => new(db.ConnectionString, "notes", epoch, "v1", [new("dbo", "Source", "Id")]);
            async Task Publish(SqlConnection c, SqlTransaction t, ChangeTrackingBatch _, CancellationToken ct)
            {
                await using var command = new SqlCommand("DELETE dbo.Projection; INSERT dbo.Projection SELECT * FROM dbo.Source", c, t);
                await command.ExecuteNonQueryAsync(ct);
            }
            var capture = Capture("before-restore"); await capture.StageAsync(); await capture.PublishAsync(Publish);
            await using var admin = new SqlConnection(new SqlConnectionStringBuilder(SqlServerDatabase.AdminConnectionString) { InitialCatalog = "master" }.ConnectionString);
            await admin.OpenAsync();
            await using (var command = new SqlCommand($"BACKUP DATABASE [{db.Name}] TO DISK=@path WITH INIT", admin))
            { command.Parameters.AddWithValue("@path", backup); await command.ExecuteNonQueryAsync(); }
            await db.ExecuteAsync("UPDATE dbo.Source SET Value=20; INSERT dbo.Source VALUES(2,99)");
            await capture.StageAsync(); await capture.PublishAsync(Publish);
            Assert.Equal(119, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Projection"));
            SqlConnection.ClearPool(new SqlConnection(db.ConnectionString));
            await using (var command = new SqlCommand($"ALTER DATABASE [{db.Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [{db.Name}] FROM DISK=@path WITH REPLACE; ALTER DATABASE [{db.Name}] SET MULTI_USER", admin))
            { command.Parameters.AddWithValue("@path", backup); await command.ExecuteNonQueryAsync(); }
            Assert.Equal(10, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Source"));
            // The epoch is deliberately held outside the restored DB. A plausible restored CT cursor alone is insufficient.
            var restored = Capture("after-restore");
            Assert.Equal("epoch-or-schema-changed", (await restored.GetHealthAsync()).Reason);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restored.StageAsync());
            await restored.BeginResnapshotAsync("before-restore");
            Assert.True((await restored.StageAsync()).InitialSnapshot);
            await db.ExecuteAsync("UPDATE dbo.Source SET Value=30");
            await restored.PublishAsync(Publish);
            Assert.Equal(30, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Projection"));
            Assert.Single((await restored.StageAsync()).Keys); await restored.PublishAsync(Publish);
            Assert.Equal(30, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Projection"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RelatedDeletesMovesAndMembershipUseDurableReverseMapping()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET CHANGE_TRACKING=ON (CHANGE_RETENTION=2 DAYS,AUTO_CLEANUP=ON)");
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Parents(Id int NOT NULL PRIMARY KEY, Visible bit NOT NULL);
            CREATE TABLE dbo.Children(Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL, Value int NOT NULL);
            ALTER TABLE dbo.Parents ENABLE CHANGE_TRACKING; ALTER TABLE dbo.Children ENABLE CHANGE_TRACKING;
            INSERT dbo.Parents VALUES(1,1),(2,1); INSERT dbo.Children VALUES(10,1,5),(11,1,7),(12,2,9);
            CREATE TABLE dbo.Projection(Id int NOT NULL PRIMARY KEY, Value int NOT NULL);
            CREATE TABLE dbo.ReverseMap(Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL);
            """);
        await db.ExecuteAsync(SqlServerChangeTrackingCapture.MigrationSql);
        var capture = new SqlServerChangeTrackingCapture(db.ConnectionString, "joined", "epoch", "v1",
            [new("dbo", "Parents", "Id"), new("dbo", "Children", "Id")]);
        async Task Publish(SqlConnection connection, SqlTransaction tx, ChangeTrackingBatch batch, CancellationToken ct)
        {
            // ReverseMap is destination-owned and updated in the same transaction as publication and the cursor.
            // Deleted child keys and changed parent relationships must reach both the old and new parent.
            var parents = new HashSet<int>();
            if (batch.InitialSnapshot)
            {
                await using var clear = new SqlCommand("DELETE dbo.Projection; DELETE dbo.ReverseMap", connection, tx);
                await clear.ExecuteNonQueryAsync(ct);
            }
            foreach (var key in batch.Keys)
            {
                if (key.Table == "Parents") parents.Add(int.Parse(key.Key));
                else
                {
                    await using var map = new SqlCommand("SELECT ParentId FROM dbo.Children WHERE Id=@id UNION SELECT ParentId FROM dbo.ReverseMap WHERE Id=@id", connection, tx);
                    map.Parameters.AddWithValue("@id", int.Parse(key.Key));
                    await using var reader = await map.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) parents.Add(reader.GetInt32(0));
                }
            }
            foreach (var id in parents.Order())
            {
                await using var project = new SqlCommand("""
                    DELETE dbo.Projection WHERE Id=@id;
                    INSERT dbo.Projection SELECT p.Id,COALESCE(SUM(c.Value),0) FROM dbo.Parents p
                    LEFT JOIN dbo.Children c ON c.ParentId=p.Id WHERE p.Id=@id AND p.Visible=1 GROUP BY p.Id;
                    """, connection, tx);
                project.Parameters.AddWithValue("@id", id); await project.ExecuteNonQueryAsync(ct);
            }
            foreach (var key in batch.Keys.Where(k => k.Table == "Children"))
            {
                await using var map = new SqlCommand("DELETE dbo.ReverseMap WHERE Id=@id; INSERT dbo.ReverseMap SELECT Id,ParentId FROM dbo.Children WHERE Id=@id", connection, tx);
                map.Parameters.AddWithValue("@id", int.Parse(key.Key)); await map.ExecuteNonQueryAsync(ct);
            }
        }
        await capture.StageAsync(); await capture.PublishAsync(Publish);
        Assert.Equal(12, await db.ScalarAsync("SELECT Value FROM dbo.Projection WHERE Id=1"));
        await db.ExecuteAsync("DELETE dbo.Children WHERE Id=10; UPDATE dbo.Children SET ParentId=2 WHERE Id=11; UPDATE dbo.Parents SET Visible=0 WHERE Id=2;");
        var pending = await capture.StageAsync();
        Assert.Contains(pending.Keys, k => k.Table == "Children" && k.Key == "10" && k.Deleted);
        await Assert.ThrowsAsync<IOException>(() => capture.PublishAsync(async (c,t,b,ct) => { await Publish(c,t,b,ct); throw new IOException("rollback reverse mapping too"); }));
        Assert.Equal(3, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.ReverseMap"));
        await capture.PublishAsync(Publish);
        Assert.Equal(0, await db.ScalarAsync("SELECT Value FROM dbo.Projection WHERE Id=1"));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.Projection WHERE Id=2"));
        Assert.Equal(2, await db.ScalarAsync("SELECT ParentId FROM dbo.ReverseMap WHERE Id=11"));
        await db.ExecuteAsync("UPDATE dbo.Parents SET Visible=1 WHERE Id=2");
        await capture.StageAsync(); await capture.PublishAsync(Publish);
        Assert.Equal(16, await db.ScalarAsync("SELECT Value FROM dbo.Projection WHERE Id=2"));
        await capture.StageAsync();
        await db.ExecuteAsync("ALTER TABLE dbo.Children ADD Extra int NULL");
        Assert.True((await capture.GetHealthAsync()).RequiresResnapshot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.PublishAsync(Publish));
        Assert.Equal(16, await db.ScalarAsync("SELECT Value FROM dbo.Projection WHERE Id=2"));
        await capture.BeginResnapshotAsync("epoch");
        Assert.True((await capture.StageAsync()).InitialSnapshot); await capture.PublishAsync(Publish);
        Assert.Equal(16, await db.ScalarAsync("SELECT Value FROM dbo.Projection WHERE Id=2"));
    }

    [Fact]
    public async Task SealedBatchReplayRollbackDeleteCatchupAndEpochResnapshot()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET CHANGE_TRACKING=ON (CHANGE_RETENTION=2 DAYS,AUTO_CLEANUP=ON)");
        await db.ExecuteAsync("""
            CREATE TABLE dbo.Source(Id int NOT NULL PRIMARY KEY, Value int NOT NULL);
            ALTER TABLE dbo.Source ENABLE CHANGE_TRACKING;
            INSERT dbo.Source VALUES(1,10),(2,20);
            CREATE TABLE dbo.Projection(Id int NOT NULL PRIMARY KEY, Value int NOT NULL);
            INSERT dbo.Projection VALUES(99,99);
            """);
        await db.ExecuteAsync(SqlServerChangeTrackingCapture.MigrationSql);
        SqlServerChangeTrackingCapture Capture(string epoch = "epoch-one", int maximum = 100) =>
            new(db.ConnectionString, "notes", epoch, "projection-v1", [new("dbo", "Source", "Id")], maximum);
        var capture = Capture();
        Assert.Null((await capture.GetHealthAsync()).PublishedVersion);
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncCaptureState"));
        var first = await capture.StageAsync();
        var health = await capture.GetHealthAsync();
        Assert.Equal(first.Id, health.PendingBatchId); Assert.Equal(2, health.PendingKeys);
        Assert.NotNull(health.PendingSinceUtc); Assert.Null(health.Reason);
        Assert.True(first.InitialSnapshot); Assert.Equal(2, first.Keys.Count);
        Assert.Equal(first.Id, (await Capture().StageAsync()).Id);
        await db.ExecuteAsync("UPDATE dbo.Source SET Value=11 WHERE Id=1; DELETE dbo.Source WHERE Id=2; INSERT dbo.Source VALUES(3,30)");
        async Task Publish(SqlConnection c, SqlTransaction t, ChangeTrackingBatch b, CancellationToken ct)
        {
            // Neutral full replacement sample: source/staging/destination share the same database. For this small
            // fixture a complete current-state replacement also handles deltas; no partial generation is exposed.
            await using var command = new SqlCommand("DELETE dbo.Projection; INSERT dbo.Projection SELECT Id,Value FROM dbo.Source;", c, t);
            await command.ExecuteNonQueryAsync(ct);
        }
        await Assert.ThrowsAsync<IOException>(() => capture.PublishAsync(async (c,t,b,ct) => { await Publish(c,t,b,ct); throw new IOException("before atomic marker"); }));
        Assert.Equal(99, await db.ScalarAsync("SELECT Value FROM dbo.Projection WHERE Id=99"));
        Assert.Equal(first.Id, (await capture.StageAsync()).Id);
        Assert.True(await capture.PublishAsync(Publish));
        Assert.False(await capture.PublishAsync(Publish));
        Assert.Equal(41, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Projection"));
        var delta = await capture.StageAsync();
        Assert.False(delta.InitialSnapshot); Assert.Equal(first.UpperVersion, delta.LowerVersion);
        Assert.Contains(delta.Keys, k => k.Key == "2" && k.Deleted);
        Assert.Contains(delta.Keys, k => k.Key == "3" && !k.Deleted);
        await capture.PublishAsync(Publish);
        var completeHealth = await capture.GetHealthAsync();
        Assert.Null(completeHealth.PendingBatchId); Assert.NotNull(completeHealth.PublishedVersion);
        Assert.Equal("epoch-or-schema-changed", (await Capture("restored-epoch").GetHealthAsync()).Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Capture("restored-epoch").StageAsync());
        var restored = Capture("restored-epoch");
        await restored.BeginResnapshotAsync("epoch-one");
        Assert.True((await restored.StageAsync()).InitialSnapshot);
        await restored.PublishAsync(Publish);
        Assert.Equal(3, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncCaptureBatches WHERE Applied=1"));
        await db.ExecuteAsync("TRUNCATE TABLE dbo.Source");
        Assert.True((await restored.GetHealthAsync()).RequiresResnapshot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.StageAsync());
        await restored.BeginResnapshotAsync("restored-epoch");
        Assert.Empty((await restored.StageAsync()).Keys);
        await restored.PublishAsync(Publish);
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.Projection"));
    }

    [Fact]
    public async Task OverBudgetAndMissingPrerequisitesNeverAdvanceCursor()
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync(SqlServerChangeTrackingCapture.MigrationSql);
        var capture = new SqlServerChangeTrackingCapture(db.ConnectionString, "notes", "epoch", "v1", [new("dbo", "Source", "Id")], 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StageAsync());
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET CHANGE_TRACKING=ON (CHANGE_RETENTION=2 DAYS,AUTO_CLEANUP=ON)");
        await db.ExecuteAsync("CREATE TABLE dbo.Source(Id int NOT NULL PRIMARY KEY); ALTER TABLE dbo.Source ENABLE CHANGE_TRACKING; INSERT dbo.Source VALUES(1),(2);");
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StageAsync());
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncCaptureState"));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncCaptureBatches"));
    }
}
