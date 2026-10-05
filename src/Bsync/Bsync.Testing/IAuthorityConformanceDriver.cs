namespace Bsync.Testing;

/// <summary>
/// Creates authorities for <see cref="AuthorityConformance"/>. Implement it once per authority (and per way of
/// reaching it, see <see cref="HttpAuthorityDriver"/>); <see cref="InMemoryAuthorityDriver"/> is the reference.
/// </summary>
public interface IAuthorityConformanceDriver
{
    /// <summary>The optional behaviour this driver can exercise.</summary>
    AuthorityCapabilities Capabilities { get; }

    /// <summary>Creates a new, empty authority configured as <paramref name="options"/> says. Each call returns an independent authority.</summary>
    Task<AuthorityUnderTest> CreateAsync(AuthorityConformanceOptions options, CancellationToken cancellationToken = default);
}
