namespace Bsync.Storage;

/// <summary>Reference orchestration store for tests. Not durable; share an instance to model concurrent owners.</summary>
public sealed class InMemoryOrchestrationStore(TimeProvider? timeProvider = null) : IOrchestrationFeatureStore
{
    /// <inheritdoc />
    public Task RequireFeatureVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (version is < 1 or > 2) throw new LocalStoreUnavailableException("outdated", "Unsupported feature version.");
        return Task.CompletedTask; // There is no persistent format to downgrade in this test provider.
    }
    private readonly Dictionary<string, OrchestrationRecord> _records = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Key(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_records) return Task.FromResult(_records.GetValueOrDefault(key));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Page(prefix, afterKey, limit);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_records) return Task.FromResult<IReadOnlyList<OrchestrationRecord>>(_records.Values
            .Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal) && (afterKey is null || string.CompareOrdinal(r.Key, afterKey) > 0))
            .OrderBy(r => r.Key, StringComparer.Ordinal).Take(limit).ToArray());
    }

    /// <inheritdoc />
    public Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Batch(mutations, fence);
        lock (_records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fence is not null && (!_records.TryGetValue(fence.Key, out var lease) || lease.Payload != fence.Token
                || lease.ExpiresAtUnixMilliseconds is not { } expiry || expiry <= _time.GetUtcNow().ToUnixTimeMilliseconds())) return Task.FromResult(false);
            if (mutations.Any(m => _records.GetValueOrDefault(m.Key)?.Revision != m.ExpectedRevision)) return Task.FromResult(false);
            foreach (var change in mutations)
            {
                if (change.Payload is null) _records.Remove(change.Key);
                else _records[change.Key] = new(change.Key, Guid.NewGuid().ToString("N"), change.Payload, change.ExpiresAtUnixMilliseconds);
            }
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_records) _records.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
