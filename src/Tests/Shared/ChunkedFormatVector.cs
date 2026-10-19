using System.Security.Cryptography;
using System.Text.Json;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Tests.Fixtures;

// Shared test consumer, run against native AES-GCM and browser Web Crypto, including packaged providers.
internal static class ChunkedFormatVector
{
    public static async Task RunAsync(IBlobCache raw, IOrchestrationStore metadata, IBlobCacheLock operations,
        IAccountResourceLock lifetimes, BlobEncryption crypto)
    {
        using var resource = typeof(ChunkedFormatVector).Assembly.GetManifestResourceStream("chunked-v2-vector.json")!;
        using var document = await JsonDocument.ParseAsync(resource);
        var vector = document.RootElement;
        var account = new AccountScope("vectors", "test", "alice");
        if (account.StorageKey != vector.GetProperty("AccountStorageKey").GetString()) throw new Exception("Independent account framing mismatch.");
        foreach (var chunk in vector.GetProperty("Chunks").EnumerateArray())
        {
            var stored = await raw.ImportAsync(new MemoryStream(Convert.FromBase64String(chunk.GetProperty("Ciphertext").GetString()!)));
            if (stored.Sha256 != chunk.GetProperty("Id").GetString()) throw new Exception("Vector chunk identity mismatch.");
        }
        var hash = vector.GetProperty("Hash").GetString()!;
        var stateKey = "encrypted-blob/v2/" + hash;
        if (!await metadata.TryWriteAsync([new(stateKey, null, vector.GetProperty("State").GetString())])) throw new Exception("Vector state collision.");
        var cache = new ChunkedEncryptedBlobCache(new BlobCacheChunkStore(raw), metadata, operations, lifetimes,
            new VectorKeys(), new(account, "vector", 1), crypto);
        await using (var stream = (await cache.OpenReadAsync(hash))!)
        {
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)) != hash) throw new Exception("Independent format hash mismatch.");
            stream.Position = 65530; var range = new byte[9]; await stream.ReadExactlyAsync(range);
            if (!range.SequenceEqual(Enumerable.Range(65530, 9).Select(i => (byte)(i % 251)))) throw new Exception("Independent range mismatch.");
        }
        var record = (await metadata.ReadAsync(stateKey))!;
        await metadata.TryWriteAsync([new(stateKey, record.Revision, vector.GetProperty("InvalidOrderState").GetString())]);
        try { await using var unexpected = await cache.OpenReadAsync(hash); throw new Exception("Authenticated reordered manifest accepted."); }
        catch (InvalidDataException) { }
    }
    private sealed class VectorKeys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LocalKeyAccess.Available(reference, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));
    }
}
