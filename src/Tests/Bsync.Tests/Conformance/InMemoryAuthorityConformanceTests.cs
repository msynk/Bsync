using Bsync.Testing;
using Xunit;

namespace Bsync.Tests.Conformance;

public sealed class InMemoryAuthorityConformanceTests : AuthorityConformanceTests
{
    protected override IAuthorityConformanceDriver Driver { get; } = new InMemoryAuthorityDriver();

    [Theory]
    [MemberData(nameof(CaseNames))]
    public Task Conformance(string name) => RunAsync(name);
}
