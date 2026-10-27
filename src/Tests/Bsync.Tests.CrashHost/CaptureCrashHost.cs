using Bsync.Server.SqlServer.Workflows;
using Microsoft.Data.SqlClient;

internal static class CaptureCrashHost
{
    internal static async Task RunAsync(string phase)
    {
        var connection = Environment.GetEnvironmentVariable("BSYNC_CAPTURE_TEST_CONNECTION") ?? throw new InvalidOperationException("Missing private test database.");
        var capture = new SqlServerChangeTrackingCapture(connection, "notes", "epoch", "v1", [new("dbo", "Source", "Id")]);
        Console.WriteLine("starting");
        await capture.StageAsync();
        if (phase == "stage") { Console.WriteLine("staged"); await Task.Delay(Timeout.Infinite); }
        await capture.PublishAsync(async (c, tx, _, ct) =>
        {
            await new SqlCommand("DELETE dbo.Projection; INSERT dbo.Projection SELECT * FROM dbo.Source", c, tx).ExecuteNonQueryAsync(ct);
            if (phase == "publish-before") { Console.WriteLine("publishing"); await Task.Delay(Timeout.Infinite, ct); }
        });
        Console.WriteLine("published"); await Task.Delay(Timeout.Infinite);
    }
}
