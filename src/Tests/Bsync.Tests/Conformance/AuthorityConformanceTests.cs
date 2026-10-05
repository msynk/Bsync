using Bsync.Testing;
using Xunit;

namespace Bsync.Tests.Conformance;

/// <summary>
/// Runs the public <see cref="AuthorityConformance"/> cases (package <c>Bsync.Testing</c>) against a driver. An authority is
/// not supported until every case passes. Multi-instance, restart and commit-ordering drills are added per provider.
/// </summary>
public abstract class AuthorityConformanceTests
{
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var conformanceCase in AuthorityConformance.Cases)
        {
            data.Add(conformanceCase.Name);
        }

        return data;
    }

    /// <summary>The driver under test. Every in-repository driver supports every capability, so every case runs.</summary>
    protected abstract IAuthorityConformanceDriver Driver { get; }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public Task Conformance(string name)
    {
        var conformanceCase = AuthorityConformance.Cases.Single(c => c.Name == name);
        Assert.True(
            (conformanceCase.Requires & ~Driver.Capabilities) == 0,
            $"The driver does not declare {conformanceCase.Requires & ~Driver.Capabilities}; in-repository drivers must run every case.");
        return conformanceCase.RunAsync(Driver);
    }
}
