using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

internal static class CaptureOrchestration
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var account = new AccountScope("fixtures", "published040", "alice");
        var metadataPath = Path.Combine(directory, "orchestration.db");
        if (File.Exists(metadataPath)) throw new IOException("Capture requires a fresh destination.");
        await using (var metadata = await SqliteOrchestrationStore.OpenAsync(metadataPath, account))
        {
            var locks = new FileBlobCacheLock(Path.Combine(directory, "locks"));
            var backing = new FileBlobCache(Path.Combine(directory, "plaintext"));
            var cache = new ManagedBlobCache(backing, metadata, locks, 100000, 100000);
            using var http = new HttpClient { BaseAddress = new("https://published040.invalid/") };
            var transfers = new BlobTransferManager(metadata, cache, new(http));
            var packages = new OfflinePackageManager(metadata, cache, transfers, locks, (_, _) => Task.FromResult(true));
            var content = await cache.ImportPinnedAsync(new MemoryStream([1, 2, 3, 4]), "draft/fixture");
            await packages.StageAsync(new(1, "manual", "one", null, [new("page", content.Sha256, content.Size, true, [])]));
            await packages.PrepareAsync("manual", TimeSpan.FromSeconds(5));
            var pendingHash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 5, 6, 7 }));
            await transfers.EnqueueAsync(pendingHash, 3, BlobTransferDirection.Download);
            await cache.AppendPartialAsync(pendingHash, new byte[] { 5 });
            var encrypted = new EncryptedBlobCache(new FileBlobCache(Path.Combine(directory, "encrypted")), metadata,
                new FileBlobCacheLock(Path.Combine(directory, "encrypted-locks")), new FixtureKeys(), new(account, "blobs", 1), new PlatformBlobEncryption());
            await encrypted.ImportAsync(new MemoryStream([11, 12, 13, 14]));
            var encryptedPartial = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 21, 22, 23 }));
            await encrypted.AppendPartialAsync(encryptedPartial, new byte[] { 21, 22 });
            var model = new LocalReadModel(metadata, "tasks");
            await model.RebuildAsync("1", "first", Rows("old"), TimeSpan.FromSeconds(5));
            await model.RebuildAsync("2", "second", Rows("new"), TimeSpan.FromSeconds(5));
            await metadata.TryWriteAsync([new("fixture-owner", null, "published-token", DateTimeOffset.Parse("2035-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds())]);
            var records = await metadata.ReadPageAsync("", null, 1000);
            await File.WriteAllTextAsync(Path.Combine(directory, "records.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            var assemblies = new[] { typeof(IBlobCache).Assembly, typeof(SqliteOrchestrationStore).Assembly }.Select(a => new
            {
                Name = a.GetName().Name,
                Version = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a.Location))),
            });
            await File.WriteAllTextAsync(Path.Combine(directory, "assemblies.json"), JsonSerializer.Serialize(assemblies, new JsonSerializerOptions { WriteIndented = true }));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
    private static async IAsyncEnumerable<ReadModelRow> Rows(string payload) { yield return new("row", payload); await Task.CompletedTask; }
    private sealed class FixtureKeys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)7, 32).ToArray()));
    }
}
