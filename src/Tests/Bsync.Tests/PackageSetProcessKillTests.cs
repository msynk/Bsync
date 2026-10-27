using System.Diagnostics;
using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class PackageSetProcessKillTests
{
    [Theory]
    [InlineData("stage", "before")]
    [InlineData("stage", "after")]
    [InlineData("activate", "before")]
    [InlineData("activate", "after")]
    public async Task RootAndActivationSurviveRealProcessDeath(string phase, string boundary)
    {
        var directory = Directory.CreateTempSubdirectory("bsync-set-kill-").FullName;
        var path = Path.Combine(directory, "metadata.db");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "package-set", path, phase, boundary }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.True(line == "package-boundary", line ?? await process.StandardError.ReadToEndAsync(timeout.Token));
            process.Kill(true); await process.WaitForExitAsync(timeout.Token);
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, new("crash", "test", "account"));
            var locks = new FileBlobCacheLock(path + ".locks");
            var cache = new ManagedBlobCache(new FileBlobCache(path + ".bytes"), metadata, locks, 1024, 1024);
            using var http = new HttpClient { BaseAddress = new("https://offline.invalid/") };
            var manager = new OfflinePackageManager(metadata, cache, new(metadata, cache, new(http)), locks, (_, _) => Task.FromResult(true));
            var expected = phase == "activate" && boundary == "after" ? "two" : "one";
            await using var snapshot = await manager.OpenActiveSetAsync("set");
            Assert.Equal(expected, snapshot.Revision);
            await using var content = await snapshot.OpenAsync("child", "item");
            Assert.Equal(expected == "one" ? 1 : 2, content!.ReadByte());
            var child = new OfflinePackageManifest(1, "child", "two", null, [new("item", Convert.ToHexStringLower(SHA256.HashData(new byte[] { 2 })), 1, true, [])]);
            await manager.StageSetAsync(new(1, "set", "two", null, [new("child", "two", OfflinePackageManager.GetManifestSha256(child))]), [child]);
            Assert.True(await manager.TryActivateSetAsync("set", "two"));
            Assert.Empty((await manager.GetSetStatusAsync("set")).MissingRequired);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await SqliteStorePool.DeleteDatabaseAsync(path); Directory.Delete(directory, true);
        }
    }
}
