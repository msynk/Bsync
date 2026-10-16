using Bsync.Blobs;
using Bsync.Client;
using Microsoft.JSInterop;

namespace Bsync.Blazor.Blobs;

/// <summary>Cross-tab physical cache exclusion using Web Locks. Unsupported browsers fail explicitly.</summary>
public sealed class BrowserBlobCacheLock : IBlobCacheLock, IAsyncDisposable
{
    private readonly IJSObjectReference _module;
    private readonly string _scope;
    private BrowserBlobCacheLock(IJSObjectReference module, string scope) { _module = module; _scope = scope; }
    /// <summary>Opens an account-scoped lock provider. Dispose only after all acquired locks are released.</summary>
    public static async Task<BrowserBlobCacheLock> OpenAsync(IJSRuntime js, AccountScope account, CancellationToken cancellationToken = default)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./_content/Bsync.Blazor/bsync-cache-locks.js").ConfigureAwait(false);
        return new(module, account.StorageKey);
    }
    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        FileBlobCacheLock.ValidateName(name);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryAcquireAsync(name, cancellationToken).ConfigureAwait(false) is { } held) return held;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        FileBlobCacheLock.ValidateName(name); cancellationToken.ThrowIfCancellationRequested();
        // Join the reply even after cancellation so an acquired lock cannot be stranded.
        var token = await _module.InvokeAsync<string?>("tryAcquire", _scope + "/" + name).ConfigureAwait(false);
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
            _disposed = true;
            await module.InvokeVoidAsync("release", token).ConfigureAwait(false);
        }
    }
}

