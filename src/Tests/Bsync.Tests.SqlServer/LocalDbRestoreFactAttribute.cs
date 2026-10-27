using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>This drill uses a disposable backup on the test process's filesystem, shared by local-user LocalDB.</summary>
public sealed class LocalDbRestoreFactAttribute : FactAttribute
{
    public LocalDbRestoreFactAttribute()
    {
        var configured = Environment.GetEnvironmentVariable("BSYNC_SQLSERVER");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(configured)
            || !new SqlConnectionStringBuilder(configured).DataSource.StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase))
            Skip = "The physical backup/restore drill requires Windows LocalDB sharing the test user's temporary directory; remote/container restore qualification is separate.";
    }
}
