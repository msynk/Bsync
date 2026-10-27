using System.Diagnostics;
using Bsync.Server.SqlServer.Workflows;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class CaptureProcessKillTests
{
    [Theory]
    [InlineData("stage-before")]
    [InlineData("stage-after")]
    [InlineData("publish-before")]
    [InlineData("publish-after")]
    public async Task ProcessDeathCannotAdvanceUncommittedCaptureOrPublication(string phase)
    {
        await using var db = await SqlServerDatabase.CreateAsync();
        await db.ExecuteAsync($"ALTER DATABASE [{db.Name}] SET ALLOW_SNAPSHOT_ISOLATION ON; ALTER DATABASE [{db.Name}] SET CHANGE_TRACKING=ON (CHANGE_RETENTION=2 DAYS,AUTO_CLEANUP=ON)");
        await db.ExecuteAsync(SqlServerChangeTrackingCapture.MigrationSql);
        await db.ExecuteAsync("CREATE TABLE dbo.Source(Id int NOT NULL PRIMARY KEY,Value int NOT NULL); ALTER TABLE dbo.Source ENABLE CHANGE_TRACKING; INSERT dbo.Source VALUES(1,10); CREATE TABLE dbo.Projection(Id int NOT NULL PRIMARY KEY,Value int NOT NULL); INSERT dbo.Projection VALUES(99,99)");
        await using var gate = await db.OpenAsync();
        if (phase == "stage-before")
        {
            await new SqlCommand("EXEC sys.sp_getapplock @Resource='capture-test-gate',@LockMode='Exclusive',@LockOwner='Session'", gate).ExecuteNonQueryAsync();
            await db.ExecuteAsync("CREATE TRIGGER dbo.CaptureTestGate ON dbo.BsyncCaptureKeys AFTER INSERT AS BEGIN EXEC sys.sp_getapplock @Resource='capture-test-gate',@LockMode='Shared',@LockOwner='Transaction'; END");
        }
        var application = "capture-kill-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "capture-kill", phase.StartsWith("stage", StringComparison.Ordinal) ? "stage" : phase }) start.ArgumentList.Add(value);
        start.Environment["BSYNC_CAPTURE_TEST_CONNECTION"] = new SqlConnectionStringBuilder(db.ConnectionString) { ApplicationName = application }.ConnectionString;
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            Assert.Equal("starting", await process.StandardOutput.ReadLineAsync(timeout.Token));
            if (phase == "stage-before")
            {
                while (true)
                {
                    await using var query = new SqlCommand("SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.dm_exec_sessions s ON s.session_id=l.request_session_id WHERE s.program_name=@app AND l.resource_type='APPLICATION' AND l.request_status='WAIT'", gate);
                    query.Parameters.AddWithValue("@app", application);
                    if (Convert.ToInt32(await query.ExecuteScalarAsync(timeout.Token)) != 0) break;
                    if (process.HasExited) Assert.Fail(await process.StandardError.ReadToEndAsync(timeout.Token));
                    await Task.Delay(20, timeout.Token);
                }
            }
            else Assert.Equal(phase == "stage-after" ? "staged" : phase == "publish-before" ? "publishing" : "published", await process.StandardOutput.ReadLineAsync(timeout.Token));
            process.Kill(true); await process.WaitForExitAsync(timeout.Token);
            if (phase == "stage-before")
            {
                await new SqlCommand("EXEC sys.sp_releaseapplock @Resource='capture-test-gate',@LockOwner='Session'", gate).ExecuteNonQueryAsync();
                await db.ExecuteAsync("DROP TRIGGER dbo.CaptureTestGate");
            }
            Assert.Equal(phase == "publish-after" ? 10 : 99, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Projection"));
            Assert.Equal(phase == "stage-before" ? 0 : 1, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncCaptureBatches"));
            var capture = new SqlServerChangeTrackingCapture(db.ConnectionString, "notes", "epoch", "v1", [new("dbo", "Source", "Id")]);
            if (phase != "publish-after")
            {
                await capture.StageAsync();
                Assert.True(await capture.PublishAsync(async (c, tx, _, ct) => await new SqlCommand("DELETE dbo.Projection; INSERT dbo.Projection SELECT * FROM dbo.Source", c, tx).ExecuteNonQueryAsync(ct)));
            }
            else Assert.False(await capture.PublishAsync((_, _, _, _) => throw new InvalidOperationException("Applied batch replayed.")));
            Assert.Equal(10, await db.ScalarAsync("SELECT SUM(Value) FROM dbo.Projection"));
            Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM dbo.BsyncCaptureBatches WHERE Applied=1"));
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
