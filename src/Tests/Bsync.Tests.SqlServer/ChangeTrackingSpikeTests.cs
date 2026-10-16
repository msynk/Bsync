using System.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>Bounded design experiment, not a shipped capture adapter or CDC qualification.</summary>
public sealed class ChangeTrackingSpikeTests
{
    [Fact]
    public async Task SnapshotBoundaryIncludesConcurrentCommitOnlyInCatchupAndRetainsDeletes()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await database.ExecuteAsync($"ALTER DATABASE [{database.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        await database.ExecuteAsync($"ALTER DATABASE [{database.Name}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON)");
        await database.ExecuteAsync("""
            CREATE TABLE dbo.CaptureItems(Id int NOT NULL PRIMARY KEY, Title nvarchar(100) NOT NULL);
            ALTER TABLE dbo.CaptureItems ENABLE CHANGE_TRACKING;
            INSERT dbo.CaptureItems VALUES(1,N'original'),(2,N'delete after snapshot');
            """);
        long boundary;
        await using (var source = await database.OpenAsync())
        await using (var snapshot = source.BeginTransaction(IsolationLevel.Snapshot))
        {
            await using var version = new SqlCommand("SELECT CHANGE_TRACKING_CURRENT_VERSION()", source, snapshot);
            boundary = Convert.ToInt64(await version.ExecuteScalarAsync());
            // Establish the source snapshot before another connection commits.
            await using var initial = new SqlCommand("SELECT Title FROM dbo.CaptureItems WHERE Id=1", source, snapshot);
            Assert.Equal("original", await initial.ExecuteScalarAsync());
            await database.ExecuteAsync("UPDATE dbo.CaptureItems SET Title=N'concurrent' WHERE Id=1; DELETE dbo.CaptureItems WHERE Id=2; INSERT dbo.CaptureItems VALUES(3,N'new');");
            Assert.Equal("original", await initial.ExecuteScalarAsync());
            await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.CaptureItems", source, snapshot);
            Assert.Equal(2, Convert.ToInt32(await count.ExecuteScalarAsync()));
            await snapshot.CommitAsync();
        }
        await using (var source = await database.OpenAsync())
        await using (var snapshot = source.BeginTransaction(IsolationLevel.Snapshot))
        {
            await using var valid = new SqlCommand("SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.CaptureItems'))", source, snapshot);
            Assert.True(boundary >= Convert.ToInt64(await valid.ExecuteScalarAsync()));
            await using var high = new SqlCommand("SELECT CHANGE_TRACKING_CURRENT_VERSION()", source, snapshot);
            Assert.True(Convert.ToInt64(await high.ExecuteScalarAsync()) > boundary);
            await using var changes = new SqlCommand("""
                SELECT c.Id,c.SYS_CHANGE_OPERATION,i.Title FROM CHANGETABLE(CHANGES dbo.CaptureItems,@version) c
                LEFT JOIN dbo.CaptureItems i ON i.Id=c.Id ORDER BY c.Id
                """, source, snapshot);
            changes.Parameters.AddWithValue("@version", boundary);
            await using (var rows = await changes.ExecuteReaderAsync())
            {
                Assert.True(await rows.ReadAsync()); Assert.Equal(1, rows.GetInt32(0)); Assert.Equal("U", rows.GetString(1)); Assert.Equal("concurrent", rows.GetString(2));
                Assert.True(await rows.ReadAsync()); Assert.Equal(2, rows.GetInt32(0)); Assert.Equal("D", rows.GetString(1)); Assert.True(rows.IsDBNull(2));
                Assert.True(await rows.ReadAsync()); Assert.Equal(3, rows.GetInt32(0)); Assert.Equal("I", rows.GetString(1));
                Assert.False(await rows.ReadAsync());
            }
            await snapshot.CommitAsync();
        }
        // Truncate invalidates the old synchronization boundary; an adapter must resnapshot, never skip to current.
        await database.ExecuteAsync("TRUNCATE TABLE dbo.CaptureItems");
        Assert.True(await database.ScalarAsync("SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.CaptureItems'))") > boundary);
    }
}
