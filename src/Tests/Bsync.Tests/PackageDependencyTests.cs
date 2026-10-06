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

    [Fact(DisplayName = "F1: only the optional S3 package depends on the AWS SDK; the blob routes do not")]
    public void ObjectStorageIsOptional()
    {
        var routes = Assembly.Load("Bsync.Server.AspNetCore").GetReferencedAssemblies().Select(r => r.Name!).ToList();
        var s3 = Assembly.Load("Bsync.Server.Blobs.S3").GetReferencedAssemblies().Select(r => r.Name!).ToList();

        Assert.DoesNotContain(routes, r => r.StartsWith("AWSSDK", StringComparison.Ordinal));
        Assert.Contains("AWSSDK.S3", s3);
        Assert.Contains("Bsync.Server.AspNetCore", s3);
        Assert.DoesNotContain(s3, r => r is "Microsoft.Data.SqlClient" or "Npgsql" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
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
