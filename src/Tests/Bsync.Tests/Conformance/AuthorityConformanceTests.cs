using Bsync.Testing;
using Xunit;

namespace Bsync.Tests.Conformance;

/// <summary>
/// Runs the public <see cref="AuthorityConformance"/> cases (package <c>Bsync.Testing</c>) against a driver. An authority is
/// not supported until every case passes. Multi-instance, restart and commit-ordering drills are added per provider.
/// </summary>
public abstract class AuthorityConformanceTests
{
    public static TheoryData<string> CaseNames() => CaseNamesFor(AuthorityCapabilities.All);

    /// <summary>The cases a driver with <paramref name="capabilities"/> runs.</summary>
    protected static TheoryData<string> CaseNamesFor(AuthorityCapabilities capabilities)
    {
        var data = new TheoryData<string>();
        foreach (var conformanceCase in AuthorityConformance.CasesFor(capabilities))
        {
            data.Add(conformanceCase.Name);
        }

        return data;
    }

    /// <summary>The driver under test.</summary>
    protected abstract IAuthorityConformanceDriver Driver { get; }

    /// <summary>Runs one case. Each provider class declares the theory over the cases its driver supports.</summary>
    protected Task RunAsync(string name)
    {
        var conformanceCase = AuthorityConformance.Cases.Single(c => c.Name == name);
        Assert.True(
            (conformanceCase.Requires & ~Driver.Capabilities) == 0,
            $"The driver does not declare {conformanceCase.Requires & ~Driver.Capabilities}.");
        return conformanceCase.RunAsync(Driver);
    }
}
