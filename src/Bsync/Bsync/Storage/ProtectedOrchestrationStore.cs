using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Blobs;
using Bsync.Client;

namespace Bsync.Storage;

/// <summary>
/// Opt-in AES-GCM payload protection over an account's orchestration provider. Keys, revisions, expiration and
/// explicitly unselected control records remain visible. The stable selector returns a record kind to protect,
/// or null for control records (including every execution lease). Use a dedicated store and never mix writers
/// with different selectors. This does not provide rollback detection against restored valid database snapshots.
/// Close older processes/tabs before enabling protection: selected writes permanently raise the provider feature version to 2.
/// </summary>
public sealed class ProtectedOrchestrationStore : IOrchestrationFeatureStore
{
    private const string Marker = "bsync-protected/1:";
    private readonly IOrchestrationStore _inner;
    private readonly LocalKeyReference _writeKey;
    private readonly ILocalKeyProvider _keys;
    private readonly BlobEncryption _encryption;
    private readonly Func<string, string?> _kind;
    private readonly bool _leaveOpen;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// Creates a strict reader/writer: selected plaintext records are refused until explicitly migrated with
    /// ProtectPageAsync. Protection is applied before one atomic provider batch, preserving revisions and fences.
    /// Key acquisition is outside the provider transaction and never provisions keys.
    /// </summary>
    public ProtectedOrchestrationStore(IOrchestrationStore inner, LocalKeyReference writeKey, ILocalKeyProvider keys,
        BlobEncryption encryption, Func<string, string?> recordKind, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _writeKey = writeKey ?? throw new ArgumentNullException(nameof(writeKey));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _encryption = encryption ?? throw new ArgumentNullException(nameof(encryption));
        _kind = recordKind ?? throw new ArgumentNullException(nameof(recordKind));
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public async Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var record = await _inner.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        return record is null ? null : await DecodeAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default)
    {
        var page = await _inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken).ConfigureAwait(false);
        var result = new List<OrchestrationRecord>(page.Count);
        foreach (var record in page) result.Add(await DecodeAsync(record, cancellationToken).ConfigureAwait(false));
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Batch(mutations, fence);
        if (fence is not null && Kind(fence.Key) is not null)
            throw new ArgumentException("Execution lease records must remain unselected control records.", nameof(fence));
        var encoded = new List<OrchestrationMutation>(mutations.Count);
        foreach (var mutation in mutations)
        {
            var kind = Kind(mutation.Key);
            encoded.Add(mutation.Payload is null || kind is null ? mutation :
                mutation with { Payload = await EncodeAsync(mutation.Key, kind, mutation.Payload, mutation.ExpiresAtUnixMilliseconds, cancellationToken).ConfigureAwait(false) });
        }
        // Base64, UTF-8 and envelope expansion count against the actual provider limits. Reject the whole batch.
        OrchestrationValidation.Batch(encoded, fence);
        await RequireFeatureVersionAsync(2, cancellationToken).ConfigureAwait(false);
        return await _inner.TryWriteAsync(encoded, fence, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly encrypts legacy plaintext and rotates selected encrypted records in one bounded page.
    /// Returns the last scanned key, or null when done. Each record uses CAS; a concurrent update is preserved
    /// and can be revisited in another pass. Retain old keys until a complete scan finds no dependencies.
    /// Hosts must exclude legacy writers before migration; mixed or downgraded writers are unsupported.
    /// </summary>
    public async Task<string?> ProtectPageAsync(string prefix, string? afterKey, int limit = 100, CancellationToken cancellationToken = default)
    {
        var page = await _inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken).ConfigureAwait(false);
        foreach (var record in page)
        {
            if (Kind(record.Key) is not { } kind) continue;
            var plaintext = record.Payload.StartsWith("bsync-protected/", StringComparison.Ordinal)
                ? (await DecodeAsync(record, cancellationToken).ConfigureAwait(false)).Payload : record.Payload;
            var payload = await EncodeAsync(record.Key, kind, plaintext, record.ExpiresAtUnixMilliseconds, cancellationToken).ConfigureAwait(false);
            OrchestrationMutation[] mutations = [new(record.Key, record.Revision, payload, record.ExpiresAtUnixMilliseconds)];
            OrchestrationValidation.Batch(mutations, null);
            await RequireFeatureVersionAsync(2, cancellationToken).ConfigureAwait(false);
            await _inner.TryWriteAsync(mutations, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return page.Count == limit ? page[^1].Key : null;
    }

    private string? Kind(string key)
    {
        var kind = _kind(key);
        if (kind is not null) OrchestrationValidation.Key(kind);
        return kind;
    }

    private byte[] AssociatedData(string key, string kind, long? expiration, LocalKeyReference reference) =>
        Utf8.GetBytes(string.Join('\n', "bsync-protected/1", reference.Account.StorageKey, key, kind,
            reference.KeyId, reference.Version.ToString(CultureInfo.InvariantCulture), expiration?.ToString(CultureInfo.InvariantCulture) ?? "none"));

    private async Task<string> EncodeAsync(string key, string kind, string payload, long? expiration, CancellationToken token)
    {
        using var access = await _keys.AcquireAsync(_writeKey, token).ConfigureAwait(false);
        if (access.Reference != _writeKey) throw new CryptographicException("Key provider returned a different reference.");
        var bytes = Utf8.GetBytes(payload);
        try
        {
            var encrypted = await _encryption.SealAsync(access.Key, bytes, AssociatedData(key, kind, expiration, _writeKey), token).ConfigureAwait(false);
            return Marker + JsonSerializer.Serialize(new ProtectedPayload(_writeKey.KeyId, _writeKey.Version, Convert.ToBase64String(encrypted)), ProtectedPayloadJson.Default.ProtectedPayload);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<OrchestrationRecord> DecodeAsync(OrchestrationRecord record, CancellationToken token)
    {
        if (Kind(record.Key) is not { } kind) return record;
        if (!record.Payload.StartsWith(Marker, StringComparison.Ordinal))
            throw new LocalStoreUnavailableException("outdated", "Protected payload format unavailable; explicit migration is required and data was retained.");
        if (record.Payload.Length > 524288) throw new InvalidDataException("Protected payload exceeds provider limits.");
        var envelope = JsonSerializer.Deserialize(record.Payload.AsSpan(Marker.Length), ProtectedPayloadJson.Default.ProtectedPayload)
            ?? throw new InvalidDataException("Missing protected payload.");
        var reference = new LocalKeyReference(_writeKey.Account, envelope.KeyId, envelope.Version);
        using var access = await _keys.AcquireAsync(reference, token).ConfigureAwait(false);
        if (access.Reference != reference) throw new CryptographicException("Key provider returned a different reference.");
        var bytes = await _encryption.OpenAsync(access.Key, Convert.FromBase64String(envelope.Ciphertext), AssociatedData(record.Key, kind, record.ExpiresAtUnixMilliseconds, reference), token).ConfigureAwait(false);
        try { return record with { Payload = Utf8.GetString(bytes) }; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <inheritdoc />
    public Task DeleteAllAsync(CancellationToken cancellationToken = default) => _inner.DeleteAllAsync(cancellationToken);
    /// <inheritdoc />
    public Task RequireFeatureVersionAsync(int version, CancellationToken cancellationToken = default) =>
        _inner is IOrchestrationFeatureStore features ? features.RequireFeatureVersionAsync(version, cancellationToken)
            : throw new NotSupportedException("Protected payloads require an orchestration provider with a downgrade barrier.");
    /// <inheritdoc />
    public ValueTask DisposeAsync() => _leaveOpen ? ValueTask.CompletedTask : _inner.DisposeAsync();
}

internal sealed record ProtectedPayload(string KeyId, int Version, string Ciphertext);
[JsonSerializable(typeof(ProtectedPayload))]
internal partial class ProtectedPayloadJson : JsonSerializerContext;
