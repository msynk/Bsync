using System.Reflection;
using Bsync.Documents;
using Bsync.Storage;
using Bsync.Testing;
using Xunit;

namespace Bsync.PackageConsumer.Tests;

/// <summary>The conformance suites as an out-of-repository provider would run them: only the Bsync.Testing package.</summary>
public sealed class PackagedConformanceTests
{
    public static TheoryData<string> AuthorityCases()
    {
        var data = new TheoryData<string>();
        foreach (var conformanceCase in AuthorityConformance.Cases)
        {
            data.Add(conformanceCase.Name);
        }

        return data;
    }

    public static TheoryData<string> StoreCases()
    {
        var data = new TheoryData<string>();
        foreach (var conformanceCase in LocalStoreConformance.Cases)
        {
            data.Add(conformanceCase.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AuthorityCases))]
    public Task InMemoryAuthority(string name) =>
        AuthorityConformance.Cases.Single(c => c.Name == name).RunAsync(new InMemoryAuthorityDriver());

    [Theory]
    [MemberData(nameof(StoreCases))]
    public Task InMemoryStore(string name) =>
        LocalStoreConformance.Cases.Single(c => c.Name == name).RunAsync(() =>
            Task.FromResult<ILocalStore<ConformanceDocument>>(new InMemoryLocalStore<ConformanceDocument>(DocumentCloner.Json(ConformanceJsonContext.Default.ConformanceDocument))));

    [Fact]
    public void ReferencesTheRequestedPackageVersion()
    {
        var expected = typeof(PackagedConformanceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "BsyncPackageVersion").Value;
        var actual = typeof(AuthorityConformance).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith($"{expected}+", actual + "+", StringComparison.Ordinal);
    }
}
