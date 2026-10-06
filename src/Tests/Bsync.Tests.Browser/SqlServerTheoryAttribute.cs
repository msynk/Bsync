using Xunit;

namespace Bsync.Tests.Browser;

/// <summary>Skipped unless <c>BSYNC_SQLSERVER</c> names a SQL Server where the user may create databases.</summary>
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BSYNC_SQLSERVER")))
        {
            Skip = "Set BSYNC_SQLSERVER to run the tasks sample in browsers.";
        }
    }
}
