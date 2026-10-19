using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Client;
using Bsync.Storage;
using Microsoft.JSInterop;

namespace Bsync.Blazor.IndexedDb;

/// <summary>Account-scoped orchestration metadata in IndexedDB schema 1. Browser eviction remains outside durability guarantees.</summary>
public sealed class IndexedDbOrchestrationStore : IOrchestrationFeatureStore
{
    private readonly IJSObjectReference _module;
    private readonly string _name;
    private bool _disposed;

    private IndexedDbOrchestrationStore(IJSObjectReference module, string name) { _module = module; _name = name; }

    /// <summary>Opens a database whose name includes all local account boundaries. Does not contact the server.</summary>
    public static async Task<IndexedDbOrchestrationStore> OpenAsync(IJSRuntime js, AccountScope account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(account);
        var module = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./_content/Bsync.Blazor/bsync-orchestration.js").ConfigureAwait(false);
        var store = new IndexedDbOrchestrationStore(module, "bsync-orchestration-" + account.StorageKey);
        try { await store.CallAsync("initialize", cancellationToken).ConfigureAwait(false); return store; }
        catch { await module.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<string> CallAsync(string method, CancellationToken token, params object?[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        // Once a write starts, join its transaction even if the caller cancels. Abandoning the JS promise could let a
        // physical cache lock go before its metadata transaction finishes. Cancellation after commit is unknown outcome.
        var interopToken = method is "write" or "clear" or "requireFeatureVersion" ? CancellationToken.None : token;
        try { return await _module.InvokeAsync<string>(method, interopToken, new object?[] { _name }.Concat(args).ToArray()).ConfigureAwait(false); }
        catch (JSException error)
        {
            var reason = error.Message.Contains("QuotaExceededError", StringComparison.Ordinal) ? "quota"
                : error.Message.Contains("VersionError", StringComparison.Ordinal) ? "outdated" : "unavailable";
            throw new LocalStoreUnavailableException(reason, "Orchestration storage is unavailable; existing data was not discarded.", error);
        }
    }

    /// <summary>Requires a permanent feature-format barrier. Close older tabs under exclusive account ownership first; published 0.4.0 subsequently fails with VersionError.</summary>
    public async Task RequireFeatureVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        if (version is < 1 or > 2) throw new LocalStoreUnavailableException("outdated", "Unsupported feature version; data retained.");
        if (version == 2) await CallAsync("requireFeatureVersion", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Key(key);
        var json = await CallAsync("read", cancellationToken, key).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, OrchestrationJson.Default.OrchestrationRecord);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Page(prefix, afterKey, limit);
        var json = await CallAsync("page", cancellationToken, prefix, afterKey, limit).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, OrchestrationJson.Default.OrchestrationRecordArray)!;
    }

    /// <inheritdoc />
    public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Batch(mutations, fence);
        var json = JsonSerializer.Serialize(mutations.ToArray(), OrchestrationJson.Default.OrchestrationMutationArray);
        return await CallAsync("write", cancellationToken, json, fence?.Key, fence?.Token).ConfigureAwait(false) == "true";
    }

    /// <inheritdoc />
    public async Task DeleteAllAsync(CancellationToken cancellationToken = default) =>
        _ = await CallAsync("clear", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _module.DisposeAsync().ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrchestrationRecord))]
[JsonSerializable(typeof(OrchestrationRecord[]))]
[JsonSerializable(typeof(OrchestrationMutation[]))]
internal partial class OrchestrationJson : JsonSerializerContext;
