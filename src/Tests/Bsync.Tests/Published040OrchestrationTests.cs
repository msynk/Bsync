using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class Published040OrchestrationTests
{
    [Fact]
    public async Task ActualPublishedMetadataPinsPartialsAndV1EncryptionSurviveUpgrade()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bsync-upgrade040-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "published040.zip"), directory);
        var account = new AccountScope("fixtures", "published040", "alice");
        try
        {
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(Path.Combine(directory, "orchestration.db"), account);
            var expected = JsonSerializer.Deserialize<OrchestrationRecord[]>(await File.ReadAllTextAsync(Path.Combine(directory, "records.json")))!;
            Assert.Equal(expected, await metadata.ReadPageAsync("", null, 1000));
            Assert.False(await metadata.TryWriteAsync([new("probe", null, "should not commit")], new("fixture-owner", "stale")));
            Assert.Null(await metadata.ReadAsync("probe"));
            var cache = new ManagedBlobCache(new FileBlobCache(Path.Combine(directory, "plaintext")), metadata,
                new FileBlobCacheLock(Path.Combine(directory, "locks")), 100000, 100000);
            var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3, 4 }));
            Assert.Contains("draft/fixture", (await cache.GetEntryAsync(hash)).Owners);
            using var http = new HttpClient { BaseAddress = new("https://published040.invalid/") };
            var transfers = new BlobTransferManager(metadata, cache, new(http));
            Assert.Equal(BlobTransferState.Complete, (await transfers.GetAsync(hash, BlobTransferDirection.Download))!.State);
            var pendingHash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 5, 6, 7 }));
            Assert.Equal(BlobTransferState.Queued, (await transfers.GetAsync(pendingHash, BlobTransferDirection.Download))!.State);
            Assert.Equal(1, await cache.PartialLengthAsync(pendingHash));
            var model = new LocalReadModel(metadata, "tasks");
            await using var reader = (await model.OpenActiveAsync())!;
            Assert.Equal("second", reader.Generation.SourceVersion);
            Assert.Equal("new", Assert.Single((await reader.ReadPageAsync(10)).Rows).Payload);
            var keys = new FixtureKeys();
            var encrypted = new EncryptedBlobCache(new FileBlobCache(Path.Combine(directory, "encrypted")), metadata,
                new FileBlobCacheLock(Path.Combine(directory, "encrypted-locks")), keys, new(account, "blobs", 1), new PlatformBlobEncryption());
            var encryptedHash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 11, 12, 13, 14 }));
            Assert.Equal(4, await encrypted.SizeAsync(encryptedHash));
            var encryptedPartial = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 21, 22, 23 }));
            Assert.Equal(2, await encrypted.PartialLengthAsync(encryptedPartial));
            // The v2 writer is explicitly selected; migration retains the published v1 source.
            var v2 = new ChunkedEncryptedBlobCache(new BlobCacheChunkStore(new FileBlobCache(Path.Combine(directory, "v2"))), metadata,
                new FileBlobCacheLock(Path.Combine(directory, "v2-operations")), new FileAccountResourceLock(Path.Combine(directory, "v2-lifetimes")), keys,
                new(account, "blobs", 2), new PlatformBlobEncryption());
            await v2.MigrateAsync(encrypted, encryptedHash);
            Assert.Equal(4, await v2.SizeAsync(encryptedHash));
            Assert.Equal(4, await encrypted.SizeAsync(encryptedHash));
            Assert.Equal(2, await encrypted.PartialLengthAsync(encryptedPartial));
            await using var migrated = (await v2.OpenReadAsync(encryptedHash))!;
            Assert.Equal(encryptedHash, Convert.ToHexStringLower(await SHA256.HashDataAsync(migrated)));
        }
        finally { SqliteStorePool.Release(Path.Combine(directory, "orchestration.db")); Directory.Delete(directory, true); }
    }
    private sealed class FixtureKeys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)7, 32).ToArray()));
    }
}
