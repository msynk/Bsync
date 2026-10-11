using System.Reflection;
using Xunit;

namespace Bsync.Tests;

/// <summary>ADR-012: Bsync (core, client and HTTP transport) carries no UI framework, so native hosts without Blazor can use it.</summary>
public sealed class PackageDependencyTests
{
    [Fact(DisplayName = "Bsync references neither ASP.NET Core, Blazor nor JS interop")]
    public void CoreIsUiIndependent()
    {
        var references = Assembly.Load("Bsync").GetReferencedAssemblies().Select(r => r.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || r.StartsWith("Microsoft.JSInterop", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r is "Microsoft.Data.SqlClient" or "Npgsql" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("AWSSDK", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "F1: the blob routes depend on no object-storage SDK (S3 is an application recipe)")]
    public void ObjectStorageIsOptional()
    {
        var routes = Assembly.Load("Bsync.Server.AspNetCore").GetReferencedAssemblies().Select(r => r.Name!).ToList();

        Assert.DoesNotContain(routes, r => r.StartsWith("AWSSDK", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ADR-016: the SQLite store leaves the native library (plain or SQLCipher) to the application")]
    public void SqliteStoreCarriesNoNativeBundle()
    {
        var references = Assembly.Load("Bsync.Storage.Sqlite").GetReferencedAssemblies().Select(r => r.Name!).ToList();

        Assert.Contains("Microsoft.Data.Sqlite", references);
        Assert.DoesNotContain(references, r => r.StartsWith("SQLitePCLRaw.batteries", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ADR-014: the SQL Server authority depends on Microsoft.Data.SqlClient only, not on EF Core, ASP.NET Core or another provider")]
    public void SqlServerProviderIsMinimal()
    {
        var references = Assembly.Load("Bsync.Server.SqlServer").GetReferencedAssemblies().Select(r => r.Name!).ToList();

        Assert.Contains("Microsoft.Data.SqlClient", references);
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || r is "Npgsql" or "Bsync.Server.PostgreSql");
    }
}
