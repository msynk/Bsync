using Bsync.Client;
using Microsoft.JSInterop;

namespace Bsync.Blazor.IndexedDb;

/// <summary>Cross-tab shared account access and exclusive deletion using Web Locks. Unsupported browsers fail explicitly.</summary>
public sealed class BrowserAccountResourceLock : IAccountResourceLock, IAsyncDisposable
{
    private readonly IJSObjectReference _module;
    private BrowserAccountResourceLock(IJSObjectReference module) => _module = module;

    /// <summary>Opens a provider. Dispose only after all acquired account lifetimes end.</summary>
    public static async Task<BrowserAccountResourceLock> OpenAsync(IJSRuntime js, CancellationToken cancellationToken = default) =>
        new(await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./_content/Bsync.Blazor/bsync-cache-locks.js").ConfigureAwait(false));

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(AccountScope account, bool exclusive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account); cancellationToken.ThrowIfCancellationRequested();
        // Do not abandon an interop reply that might have acquired a physical lifetime lock.
        var token = await _module.InvokeAsync<string?>("tryAcquireAccount", account.StorageKey, exclusive).ConfigureAwait(false);
        return token is null ? null : new Held(_module, token);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _module.DisposeAsync();
    private sealed class Held(IJSObjectReference module, string token) : IAsyncDisposable
    {
        private bool _disposed;
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            await module.InvokeVoidAsync("release", token).ConfigureAwait(false);
            _disposed = true;
        }
    }
}
