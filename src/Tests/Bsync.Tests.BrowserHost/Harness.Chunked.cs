using System.Security.Cryptography;
using Bsync.Blazor.Blobs;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Microsoft.JSInterop;

namespace Bsync.Tests.BrowserHost;

public static partial class Harness
{
    [JSInvokable(nameof(ChunkScrubReloadChecks))]
    public static async Task<string> ChunkScrubReloadChecks(string name, bool reopened)
    {
        var account = new AccountScope("tests", "browser-scrub", name);
        await using var metadata = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        await using var operations = await BrowserBlobCacheLock.OpenAsync(_js, account);
        await using var lifetimes = await BrowserAccountResourceLock.OpenAsync(_js);
        await using var crypto = await BrowserBlobEncryption.OpenAsync(_js);
        var backing = await BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var cache = new ChunkedEncryptedBlobCache(new BlobCacheChunkStore(backing), metadata, operations, lifetimes,
            new BlobTestKeys(), new(account, "chunks", 1), crypto);
        var bytes = new byte[2 * 1048576 + 17]; Array.Fill(bytes, (byte)23);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!reopened)
        {
            await cache.ImportAsync(new MemoryStream(bytes));
            var first = await cache.ScrubAsync(hash, TimeSpan.FromSeconds(30), 1048576);
            if (first.Status != ChunkScrubStatus.Deferred || first.VerifiedChunks != 1) throw new Exception("Initial browser scrub progress failed.");
            return "pending";
        }
        var resumed = await cache.ScrubAsync(hash, TimeSpan.FromSeconds(30), 1048576);
        if (resumed.VerifiedChunks != 2 || resumed.VerifiedBytes != 2 * 1048576) throw new Exception("Browser reload lost scrub progress.");
        var complete = await cache.ScrubAsync(hash, TimeSpan.FromSeconds(30), 17);
        if (complete.Status != ChunkScrubStatus.Complete || complete.VerifiedBytes != bytes.Length) throw new Exception("Resumed browser scrub did not finish.");
        var keys = (await cache.GetKeyRetentionPageAsync()).Generations;
        if (keys.Count != 1 || keys[0].Key?.Version != 1) throw new Exception("Browser reload lost key obligation.");
        await metadata.DeleteAllAsync();
        await BrowserBlobStore.DeleteAllAsync(_js, account.StorageKey);
        return "ok";
    }

    [JSInvokable(nameof(ChunkedChecks))]
    public static async Task<string> ChunkedChecks(string name, bool reopened)
    {
        var account = new AccountScope("tests", "browser", name);
        await using var metadata = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        await using var operations = await BrowserBlobCacheLock.OpenAsync(_js, account);
        await using var lifetimes = await BrowserAccountResourceLock.OpenAsync(_js);
        await using var crypto = await BrowserBlobEncryption.OpenAsync(_js);
        var backing = await BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var keys = new BlobTestKeys();
        ChunkedEncryptedBlobCache Cache(int version) => new(new BlobCacheChunkStore(backing), metadata, operations, lifetimes, keys, new(account, "chunks", version), crypto);
        // Beyond the v1 limit, with a non-aligned tail and a partial reload boundary.
        var bytes = new byte[17 * 1048576 + 31];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var cache = Cache(1);
        if (!reopened)
        {
            await MaintenanceChecks(name, false);
            await cache.AppendPartialAsync(hash, bytes.AsMemory(0, 1048607));
            return account.StorageKey;
        }
        if (await cache.PartialLengthAsync(hash) != 1048607) throw new Exception("V2 partial did not survive reload.");
        await cache.AppendPartialAsync(hash, bytes.AsMemory(1048607));
        if (!await cache.CompletePartialAsync(hash)) throw new Exception("V2 completion failed.");
        var scrub = await cache.ScrubAsync(hash, TimeSpan.FromSeconds(30), 1048576);
        if (scrub.VerifiedChunks != 1 || scrub.Status != ChunkScrubStatus.Deferred) throw new Exception("Browser scrub checkpoint failed.");
        var resumedScrub = await Cache(1).ScrubAsync(hash, TimeSpan.FromSeconds(30), long.MaxValue);
        if (resumedScrub.Status != ChunkScrubStatus.Complete || resumedScrub.VerifiedBytes != bytes.Length) throw new Exception("Browser scrub resume failed.");
        var old = (await cache.OpenReadAsync(hash))!;
        await Cache(2).RotateAsync(hash);
        await cache.CollectAsync();
        var retainedKeys = (await cache.GetKeyRetentionPageAsync()).Generations;
        if (!retainedKeys.Any(g => g.Key?.Version == 1) || !retainedKeys.Any(g => g.Key?.Version == 2)) throw new Exception("Reader-held key obligation lost.");
        old.Position = 1048570;
        var range = new byte[32]; await old.ReadExactlyAsync(range);
        if (!range.AsSpan().SequenceEqual(bytes.AsSpan(1048570, 32))) throw new Exception("Old reader crossed generations.");
        await old.DisposeAsync();
        await Cache(2).CollectAsync();
        if ((await cache.GetKeyRetentionPageAsync()).Generations.Any(g => g.Key?.Version != 2)) throw new Exception("Collected key obligation leaked.");
        if (await metadata.ReadAsync("encrypted-scrub/v1/" + scrub.Generation) is not null) throw new Exception("Retired scrub checkpoint leaked.");
        await using (var current = (await Cache(2).OpenReadAsync(hash))!)
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(current)) != hash) throw new Exception("V2 hash mismatch.");
        if (await cache.GetStoredBytesAsync() != bytes.Length + 18L * 28) throw new Exception("Retired browser chunks leaked.");
        var firstPage = await backing.ListCompletedPageAsync(limit: 7);
        var secondPage = await backing.ListCompletedPageAsync(firstPage[^1], 7);
        if (firstPage.Count != 7 || secondPage.Count != 7 || firstPage.Intersect(secondPage).Any()) throw new Exception("Browser inventory pagination failed.");
        var small = await cache.ImportAsync(new MemoryStream([1, 2, 3]));
        await cache.RecoverAsync(small.Sha256, new MemoryStream([1, 2, 3]));
        long reserved = 0, quarantined = 0; string? cursor = null;
        do
        {
            var observation = await cache.ObserveStoragePageAsync(cursor, 7);
            reserved += observation.ReservedBytes; quarantined += observation.QuarantinedBytes;
            cursor = observation.Continuation;
        } while (cursor is not null);
        if (quarantined != 31 || reserved != bytes.Length + 18L * 28 + 62) throw new Exception("Browser quarantine inventory mismatch.");
        await Bsync.Tests.Fixtures.ChunkedFormatVector.RunAsync(backing, metadata, operations, lifetimes, crypto);

        await using var protectedStore = new ProtectedOrchestrationStore(metadata, new(account, "payloads", 1), keys, crypto,
            new OrchestrationPayloadPolicy().Protect("private/", "workflow").Control("control-owner").CreateSelector(), leaveOpen: true);
        await protectedStore.TryWriteAsync([new("control-owner", null, "lease", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()), new("private/a", null, "application data")]);
        var record = (await protectedStore.ReadAsync("private/a"))!;
        if (await protectedStore.TryWriteAsync([new(record.Key, record.Revision, "stale"), new("private/b", null, "partial")], new("control-owner", "stale"))) throw new Exception("Encrypted stale fence accepted.");
        if (await metadata.ReadAsync("private/b") is not null || (await protectedStore.ReadAsync(record.Key))!.Payload != "application data") throw new Exception("Encrypted atomicity failed.");
        if ((await metadata.ReadAsync(record.Key))!.Payload.Contains("application data", StringComparison.Ordinal)) throw new Exception("Plaintext payload persisted.");
        await MaintenanceChecks(name, true);
        await metadata.DeleteAllAsync();
        await BrowserBlobStore.DeleteAllAsync(_js, account.StorageKey);
        return "ok";
    }
}
