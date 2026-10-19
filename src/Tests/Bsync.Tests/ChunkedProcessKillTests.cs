using System.Diagnostics;
using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class ChunkedProcessKillTests
{
    [Theory]
    [InlineData("before", false)]
    [InlineData("after", false)]
    [InlineData("before", true)]
    [InlineData("after", true)]
    public async Task PublicationAndScrubSurviveRealProcessDeath(string boundary, bool scrub)
    {
        var directory = Directory.CreateTempSubdirectory("bsync-chunk-kill-").FullName;
        var path = Path.Combine(directory, "metadata.db");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), scrub ? "chunked-scrub" : "chunked-publication", path, boundary }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) Assert.Fail("Crash host exited early: " + await process.StandardError.ReadToEndAsync(timeout.Token));
            Assert.Equal("chunk-publication", line);
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
            var account = new AccountScope("crash", "test", "account");
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, account);
            var cache = new ChunkedEncryptedBlobCache(new BlobCacheChunkStore(new FileBlobCache(path + ".chunks")), metadata,
                new FileBlobCacheLock(path + ".operations"), new FileAccountResourceLock(path + ".lifetimes"), new Keys(), new(account, "chunks", 2), new PlatformBlobEncryption());
            var bytes = new byte[1048700]; Array.Fill(bytes, (byte)7); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (scrub)
            {
                var progress = await cache.ScrubAsync(hash, TimeSpan.FromSeconds(10), 1048576, cancellationToken: timeout.Token);
                Assert.Equal(boundary == "before" ? 1 : 2, progress.VerifiedChunks);
                Assert.Equal(boundary == "before" ? ChunkScrubStatus.Deferred : ChunkScrubStatus.Complete, progress.Status);
            }
            else if (boundary == "before") Assert.True(await cache.CompletePartialAsync(hash, timeout.Token));
            else Assert.Equal(0, await cache.PartialLengthAsync(hash, timeout.Token));
            await cache.CollectAsync(cancellationToken: timeout.Token);
            Assert.Equal(bytes.Length + 56, await cache.GetStoredBytesAsync(timeout.Token));
            await using var read = (await cache.OpenReadAsync(hash, timeout.Token))!;
            Assert.Equal(hash, Convert.ToHexStringLower(await SHA256.HashDataAsync(read, timeout.Token)));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await SqliteStorePool.DeleteDatabaseAsync(path); Directory.Delete(directory, true);
        }
    }
    private sealed class Keys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)reference.Version, 32).ToArray()));
    }
}
