namespace Bsync.Storage;

/// <summary>A versioned orchestration record. Payload schemas are owned and versioned by their feature.</summary>
/// <param name="Key">ASCII ordinal key.</param>
/// <param name="Revision">Opaque compare-and-write token; never reused, including after deletion.</param>
/// <param name="Payload">Opaque feature data, normally source-generated JSON. Do not store credentials.</param>
/// <param name="ExpiresAtUnixMilliseconds">Optional UTC expiration used by execution fences.</param>
public sealed record OrchestrationRecord(string Key, string Revision, string Payload, long? ExpiresAtUnixMilliseconds = null);

/// <summary>A conditional change; null payload deletes. Null expected revision requires absence.</summary>
/// <param name="Key">Record key.</param>
/// <param name="ExpectedRevision">Observed revision, or null for an absent record.</param>
/// <param name="Payload">New payload, or null to delete.</param>
/// <param name="ExpiresAtUnixMilliseconds">New optional expiration.</param>
public sealed record OrchestrationMutation(string Key, string? ExpectedRevision, string? Payload, long? ExpiresAtUnixMilliseconds = null);

/// <summary>A commit must find this lease payload and an expiration strictly later than the store's current UTC time.</summary>
/// <param name="Key">Lease record key.</param>
/// <param name="Token">Unique ownership generation, stored as the lease payload.</param>
public sealed record OrchestrationFence(string Key, string Token);

/// <summary>
/// Account-scoped durable metadata, separate from document pending operations. Implementations must atomically check
/// every revision and optional execution fence before applying all mutations, or apply none. No callbacks or network
/// operations run inside a transaction. A page is ordinal keyset pagination, not a snapshot across concurrent writes.
/// </summary>
public interface IOrchestrationStore : IAsyncDisposable
{
    /// <summary>Reads one record, including expired records.</summary>
    Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>Reads at most limit records matching prefix, strictly after afterKey. Limit is 1..1000.</summary>
    Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default);
    /// <summary>Atomically applies 1..1000 changes if revisions and fence still match. False changes nothing.</summary>
    Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default);
    /// <summary>Deletes this account's metadata only. Stop all account work before calling.</summary>
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Shared bounds and portable ASCII key rules for orchestration providers.</summary>
public static class OrchestrationValidation
{
    /// <summary>Validates a portable key or prefix (empty is allowed only for a prefix).</summary>
    public static void Key(string key, bool prefix = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        if ((!prefix && key.Length == 0) || key.Length > 256 || key.Any(c =>
            !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '/')))
            throw new ArgumentException("Orchestration keys use 1..256 ASCII letters, digits, -, _, ., ~ or /.", nameof(key));
    }

    /// <summary>Validates a bounded page.</summary>
    public static void Page(string prefix, string? afterKey, int limit)
    {
        Key(prefix, prefix: true);
        if (afterKey is not null) Key(afterKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
    }

    /// <summary>Validates a transaction; duplicate keys are forbidden and each payload is bounded to 1 MiB of UTF-16 text.</summary>
    public static void Batch(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count is < 1 or > 1000) throw new ArgumentException("A batch contains 1..1000 mutations.", nameof(mutations));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        long characters = 0;
        foreach (var mutation in mutations)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            Key(mutation.Key);
            if (!keys.Add(mutation.Key)) throw new ArgumentException("Duplicate mutation key.", nameof(mutations));
            if (mutation.Payload?.Length > 524288) throw new ArgumentException("Payload exceeds 1 MiB of UTF-16 text.", nameof(mutations));
            characters += mutation.Payload?.Length ?? 0;
            if (characters > 8388608) throw new ArgumentException("Batch exceeds 16 MiB of UTF-16 payload text.", nameof(mutations));
        }
        if (fence is not null)
        {
            Key(fence.Key);
            ArgumentException.ThrowIfNullOrEmpty(fence.Token);
        }
    }
}
