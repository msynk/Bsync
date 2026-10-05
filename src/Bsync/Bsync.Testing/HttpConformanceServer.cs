using Bsync.Server;

namespace Bsync.Testing;

/// <summary>
/// A running HTTP server that exposes an authority under test at <see cref="HttpAuthorityDriver.Collection"/> with
/// schema <see cref="HttpAuthorityDriver.SchemaId"/> and <see cref="HttpAuthorityDriver.Json"/>. The host decides how a
/// caller is authenticated; its scope resolver must map each caller to <see cref="SyncCallContext.Scope"/>.
/// </summary>
public abstract class HttpConformanceServer : IAsyncDisposable
{
    /// <summary>
    /// An <see cref="HttpClient"/> (with <see cref="HttpClient.BaseAddress"/> set) whose requests the server
    /// authenticates as <paramref name="caller"/>.
    /// </summary>
    public abstract HttpClient CreateClient(SyncCallContext caller);

    /// <summary>Stops the server.</summary>
    public abstract ValueTask DisposeAsync();
}
