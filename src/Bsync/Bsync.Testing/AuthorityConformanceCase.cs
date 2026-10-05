namespace Bsync.Testing;

/// <summary>One authority conformance case.</summary>
/// <param name="Name">Stable name, including the invariant ids it checks.</param>
/// <param name="Requires">The optional capabilities the case needs (<see cref="AuthorityCapabilities.None"/> for core cases).</param>
/// <param name="RunAsync">Runs the case against authorities created by the driver.</param>
public sealed record AuthorityConformanceCase(string Name, AuthorityCapabilities Requires, Func<IAuthorityConformanceDriver, Task> RunAsync)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}
