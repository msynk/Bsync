using Bsync.Clocks;
using Bsync.Storage;

namespace Bsync.Testing;

/// <summary>One conformance case.</summary>
/// <param name="Name">Stable name, including the invariant ids it checks.</param>
/// <param name="RunAsync">Runs the case against stores created by the factory (each call returns a new, empty store).</param>
public sealed record ConformanceCase(string Name, Func<Func<Task<ILocalStore<ConformanceDocument>>>, Task> RunAsync)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}
