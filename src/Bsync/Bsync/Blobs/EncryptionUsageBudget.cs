using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>
/// Host authority for aggregate AES-GCM invocation admission. Reservations must be durable, atomic and never
/// refunded after uncertain completion. Hosts sharing actual keys across devices must share this authority or
/// provision disjoint durable allocations whose total stays within their reviewed key-use policy.
/// </summary>
public interface IEncryptionUsageBudget
{
    /// <summary>Reserves one invocation for a domain-separated fingerprint of actual key bytes, not a key alias.</summary>
    Task ReserveAsync(string keyFingerprint, CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed durable invocation counter over an existing orchestration provider.</summary>
public sealed class OrchestrationEncryptionUsageBudget : IEncryptionUsageBudget
{
    private readonly IOrchestrationStore _store;
    private readonly long _limit;

    /// <summary>
    /// Uses a dedicated lifetime-long ledger. Do not delete it on account removal while the key remains usable,
    /// restore an older ledger, or protect it through the same budgeted encryption instance (recursive admission).
    /// The limit is host policy, not a library claim that a particular count is cryptographically safe.
    /// </summary>
    public OrchestrationEncryptionUsageBudget(IOrchestrationStore store, long maximumInvocations)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumInvocations, 1);
        _limit = maximumInvocations;
    }

    /// <inheritdoc />
    public async Task ReserveAsync(string keyFingerprint, CancellationToken cancellationToken = default)
    {
        if (keyFingerprint is null || keyFingerprint.Length != 64 || keyFingerprint.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new ArgumentException("Expected a lowercase SHA-256 key fingerprint.", nameof(keyFingerprint));
        var key = "encryption-usage/v1/" + keyFingerprint;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            var state = record is null ? new EncryptionUsageState(1, 0, _limit)
                : JsonSerializer.Deserialize(record.Payload, EncryptionUsageJson.Default.EncryptionUsageState)
                    ?? throw new InvalidDataException("Missing encryption usage state.");
            if (state.Version != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported encryption usage ledger; key use refused.");
            if (state.Used < 0 || state.Limit < 1 || state.Used > state.Limit)
                throw new InvalidDataException("Invalid encryption usage ledger; key use refused.");
            // A new process cannot raise the persisted ceiling by choosing different configuration.
            var limit = Math.Min(state.Limit, _limit);
            if (state.Used >= limit) throw new CryptographicException("Encryption usage budget exhausted; provision a new key.");
            var next = JsonSerializer.Serialize(new EncryptionUsageState(1, state.Used + 1, limit), EncryptionUsageJson.Default.EncryptionUsageState);
            // Join admitted storage commits. A cancelled caller can lose an invocation but cannot reuse one.
            if (await _store.TryWriteAsync([new(key, record?.Revision, next)], cancellationToken: CancellationToken.None).ConfigureAwait(false)) return;
        }
    }
}

/// <summary>
/// Applies invocation admission to any native/browser encryption provider. Share this decorator across chunks,
/// manifests, v1 envelopes and protected metadata using the same actual keys. Decryption consumes no invocation.
/// It does not own or dispose the wrapped provider or budget. Missing host-wide accounting is not inferred safe.
/// </summary>
public sealed class BudgetedBlobEncryption(BlobEncryption encryption, IEncryptionUsageBudget budget) : BlobEncryption
{
    private readonly BlobEncryption _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));
    private readonly IEncryptionUsageBudget _budget = budget ?? throw new ArgumentNullException(nameof(budget));

    /// <inheritdoc />
    public override async ValueTask<byte[]> SealAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (key.Length != 32) throw new ArgumentException("Expected a 256-bit key.", nameof(key));
        // Own the key across the admission await: caller mutation must not charge one key and use another.
        var copy = key.ToArray();
        try
        {
            var domain = "Bsync.encryption-use/v1\0"u8;
            var input = new byte[32 + domain.Length];
            domain.CopyTo(input);
            copy.CopyTo(input, domain.Length);
            string fingerprint;
            try { fingerprint = Convert.ToHexStringLower(SHA256.HashData(input)); }
            finally { CryptographicOperations.ZeroMemory(input); }
            await _budget.ReserveAsync(fingerprint, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await _encryption.SealAsync(copy, plaintext, associatedData, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    /// <inheritdoc />
    public override ValueTask<byte[]> OpenAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> envelope,
        ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) =>
        _encryption.OpenAsync(key, envelope, associatedData, cancellationToken);
}

internal sealed record EncryptionUsageState(int Version, long Used, long Limit);
[JsonSerializable(typeof(EncryptionUsageState))]
internal partial class EncryptionUsageJson : JsonSerializerContext;
