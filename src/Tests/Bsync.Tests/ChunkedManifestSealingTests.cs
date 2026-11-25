using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bsync.Blobs;
using Xunit;

namespace Bsync.Tests;

/// <summary>
/// Plan item A9: a chunked (v2) manifest is sealed for its role and length, so a partial manifest of the same generation,
/// captured earlier, cannot authenticate as the complete object and serve a truncated blob.
/// </summary>
public sealed partial class ChunkedEncryptedBlobCacheTests
{
    private const string StatePrefix = "encrypted-blob/v2/";

    private async Task<JsonObject> StateAsync(string hash) =>
        JsonNode.Parse((await _metadata.ReadAsync(StatePrefix + hash))!.Payload)!.AsObject();

    private async Task ReplaceStateAsync(string hash, JsonObject state)
    {
        var record = (await _metadata.ReadAsync(StatePrefix + hash))!;
        Assert.True(await _metadata.TryWriteAsync([new(StatePrefix + hash, record.Revision, state.ToJsonString())]));
    }

    [Fact(DisplayName = "A9: an earlier partial manifest put in place of the complete one does not authenticate")]
    public async Task PartialManifestCannotStandInForComplete()
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * 1048576 + 77);
        var hash = Hash(bytes);
        var cache = Cache();
        await cache.AppendPartialAsync(hash, bytes.AsMemory(0, 1048576));
        var truncated = (await StateAsync(hash))["Partial"]!.DeepClone(); // one chunk of three
        await cache.AppendPartialAsync(hash, bytes.AsMemory(1048576));
        Assert.True(await cache.CompletePartialAsync(hash));

        var state = await StateAsync(hash);
        state["Complete"] = truncated;
        await ReplaceStateAsync(hash, state);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => { await using var _ = await Cache().OpenReadAsync(hash); });
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Cache().SizeAsync(hash));
    }

    [Fact(DisplayName = "A9: a manifest whose stated length was changed does not authenticate")]
    public async Task ManifestLengthIsBound()
    {
        var bytes = RandomNumberGenerator.GetBytes(70000);
        var imported = await Cache().ImportAsync(new MemoryStream(bytes));
        var state = await StateAsync(imported.Sha256);
        state["Complete"]!["Length"] = 65536;
        await ReplaceStateAsync(imported.Sha256, state);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => { await using var _ = await Cache().OpenReadAsync(imported.Sha256); });
    }

    [Fact(DisplayName = "A9: completing an upload publishes a complete-role seal with the object's length")]
    public async Task CompletionReseals()
    {
        var bytes = RandomNumberGenerator.GetBytes(100000);
        var imported = await Cache().ImportAsync(new MemoryStream(bytes));

        var complete = (await StateAsync(imported.Sha256))["Complete"]!;

        Assert.Equal(2, complete["Sealing"]!.GetValue<int>());
        Assert.Equal(bytes.Length, complete["Length"]!.GetValue<long>());
        await AssertBytes(Cache(), imported.Sha256, bytes);
    }

    [Fact(DisplayName = "A9: a pre-0.6 complete manifest is resealed after its content was verified")]
    public async Task LegacyCompleteIsResealedOnOpen()
    {
        var bytes = RandomNumberGenerator.GetBytes(70000);
        var imported = await Cache().ImportAsync(new MemoryStream(bytes));
        // Simulate the pre-0.6 seal by sealing a legacy envelope the way 0.5 did.
        var state = await StateAsync(imported.Sha256);
        state["Complete"] = await LegacySealAsync(imported.Sha256, state["Complete"]!);
        await ReplaceStateAsync(imported.Sha256, state);

        await AssertBytes(Cache(), imported.Sha256, bytes);

        var resealed = (await StateAsync(imported.Sha256))["Complete"]!;
        Assert.Equal(2, resealed["Sealing"]!.GetValue<int>());
        await AssertBytes(Cache(), imported.Sha256, bytes);
    }

    /// <summary>Re-encrypts a sealing-2 manifest envelope with the pre-0.6 AAD (purpose <c>manifest</c>, no length).</summary>
    private async Task<JsonNode> LegacySealAsync(string hash, JsonNode envelope)
    {
        var generation = envelope["Generation"]!.GetValue<string>();
        var keyId = envelope["KeyId"]!.GetValue<string>();
        var version = envelope["KeyVersion"]!.GetValue<int>();
        var key = Enumerable.Repeat((byte)version, 32).ToArray();
        string Aad(string purpose, int length) => string.Join('\n', "Bsync.chunked/2/AES-256-GCM", purpose, Account.StorageKey, hash, generation,
            keyId, version.ToString(System.Globalization.CultureInfo.InvariantCulture), "-1", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var crypto = new PlatformBlobEncryption();
        var length = envelope["Length"]!.GetValue<long>();
        var plain = await crypto.OpenAsync(key, Convert.FromBase64String(envelope["Ciphertext"]!.GetValue<string>()),
            System.Text.Encoding.UTF8.GetBytes(Aad("manifest/complete", (int)length)));
        var sealedLegacy = await crypto.SealAsync(key, plain, System.Text.Encoding.UTF8.GetBytes(Aad("manifest", -1)));
        return new JsonObject
        {
            ["Generation"] = generation,
            ["KeyId"] = keyId,
            ["KeyVersion"] = version,
            ["Ciphertext"] = Convert.ToBase64String(sealedLegacy),
        };
    }
}
