using Bsync.Testing;

namespace Bsync.Tests.Conformance;

public sealed class InMemoryAuthorityConformanceTests : AuthorityConformanceTests
{
    protected override IAuthorityConformanceDriver Driver { get; } = new InMemoryAuthorityDriver();
}
