using System.Diagnostics;
using Bsync.Blobs;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class OrchestrationProcessKillTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task RepairSurvivesProcessDeathAroundPromotionWithoutLosingPins(string boundary)
    {
        var directory = Directory.CreateTempSubdirectory("bsync-repair-kill-").FullName;
        var path = Path.Combine(directory, "metadata.db");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "blob-repair", path, boundary }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) Assert.Fail("Crash host exited early: " + await process.StandardError.ReadToEndAsync(timeout.Token));
            Assert.Equal("repair-boundary", line);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, new("crash", "test", "account"), timeProvider: time);
            var locks = new FileBlobCacheLock(path + ".locks");
            var cache = new ManagedBlobCache(new FileBlobCache(path + ".blobs"), metadata, locks, 100, 100);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3, 4 }));
            await using (var released = await cache.AcquireTransferAsync(hash, timeout.Token)) { }
            using var http = new HttpClient(new NoRepairRequest()) { BaseAddress = new("https://repair.test/") };
            var transfers = new BlobTransferManager(metadata, cache, new(http), new() { TimeProvider = time });
            Assert.Equal(BlobRepairState.Promoting, (await transfers.GetRepairAsync(hash))!.State);
            Assert.Equal(boundary == "after" ? new byte[] { 1, 2, 3, 4 } : [9, 9, 9, 9],
                await File.ReadAllBytesAsync(Path.Combine(path + ".blobs", "objects", hash[..2], hash), timeout.Token));
            var owners = (await cache.GetEntryAsync(hash)).Owners;
            var packages = new OfflinePackageManager(metadata, cache, transfers, locks, (_, _) => Task.FromResult(true), time);
            Assert.Equal(OfflinePackageReadiness.ReadyFreshnessUnknown, (await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10), timeout.Token)).Readiness);
            Assert.Equal(BlobRepairState.Completed, (await transfers.GetRepairAsync(hash))!.State);
            Assert.Equal(BlobTransferState.Complete, (await transfers.GetAsync(hash, BlobTransferDirection.Download))!.State);
            Assert.Equal(owners.Order(), (await cache.GetEntryAsync(hash)).Owners.Order());
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await SqliteStorePool.DeleteDatabaseAsync(path);
            Directory.Delete(directory, true);
        }
    }

    private sealed class NoRepairRequest : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Exception("Verified staging or promoted bytes should recover without another request.");
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WorkflowResumesAfterProcessDeathBetweenEffectAndProgressCommit(int killedStep)
    {
        var directory = Directory.CreateTempSubdirectory("bsync-workflow-kill-").FullName;
        var path = Path.Combine(directory, "metadata.db");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "workflow", path, killedStep.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) Assert.Fail("Crash host exited early: " + await process.StandardError.ReadToEndAsync(timeout.Token));
            Assert.Equal("effect " + killedStep, line);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            var account = new Bsync.Client.AccountScope("crash", "test", "account");
            // Both store and lease observe a controlled clock after the killed owner's 30-second lease expiration.
            var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, account, timeProvider: time);
            var workflow = new Bsync.Client.RecoverableWorkflow(metadata, account, time);
            Assert.Equal(killedStep, (await workflow.GetAsync("work"))!.NextStep);
            var calls = new int[2];
            var steps = Enumerable.Range(0, 2).Select(index => new Bsync.Client.WorkflowStep("step-" + index, async (operation, ct) =>
            {
                calls[index]++;
                var key = "effects/" + index;
                if (await metadata.ReadAsync(key, ct) is { } prior) Assert.Equal(prior.Payload, operation);
                else Assert.True(await metadata.TryWriteAsync([new(key, null, operation)], cancellationToken: ct));
                return Bsync.Client.WorkflowStepOutcome.Complete;
            })).ToArray();
            Assert.Equal("complete", (await workflow.RunAsync("work", "1", steps, TimeSpan.FromSeconds(10), timeout.Token))!.State);
            Assert.Equal(2, (await metadata.ReadPageAsync("effects/", null, 10)).Count);
            Assert.Equal(killedStep == 0 ? 1 : 0, calls[0]);
            Assert.Equal(1, calls[1]);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await SqliteStorePool.DeleteDatabaseAsync(path);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExclusiveAccountDeletionWaitsForBothProcessAndLocalReaderLifetimes()
    {
        var directory = Directory.CreateTempSubdirectory("bsync-account-kill-").FullName;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "account-resource", directory }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal("account-open", await process.StandardOutput.ReadLineAsync(timeout.Token));
            var locks = new Bsync.Client.FileAccountResourceLock(directory);
            var account = new Bsync.Client.AccountScope("crash", "test", "account");
            await using (var local = await locks.TryAcquireAsync(account, exclusive: false, timeout.Token))
            {
                Assert.NotNull(local);
                Assert.Null(await locks.TryAcquireAsync(account, exclusive: true, timeout.Token));
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(timeout.Token);
                Assert.Null(await locks.TryAcquireAsync(account, exclusive: true, timeout.Token));
            }
            IAsyncDisposable? exclusive;
            while ((exclusive = await locks.TryAcquireAsync(account, exclusive: true, timeout.Token)) is null)
                await Task.Delay(25, timeout.Token); // Observe actual OS handle cleanup after process exit.
            await using (exclusive)
                Assert.Null(await locks.TryAcquireAsync(account, exclusive: false, timeout.Token));
            Assert.Single(Directory.GetFiles(directory, "*.account-lock")); // Never unlink stable lock identities.
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ReaderAndSnapshotPinsAreReclaimedOnlyAfterOwningProcessDies()
    {
        var directory = Directory.CreateTempSubdirectory("bsync-reader-kill-").FullName;
        var path = Path.Combine(directory, "metadata.db");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "managed-reader", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) Assert.Fail("Crash host exited early: " + await process.StandardError.ReadToEndAsync(timeout.Token));
            Assert.StartsWith("reader ", line);
            var hash = line[7..];
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, new("crash", "test", "account"));
            var cache = new ManagedBlobCache(new FileBlobCache(path + ".blobs"), metadata, new FileBlobCacheLock(path + ".locks"), 100, 100);
            await cache.ReconcileAsync(timeout.Token);
            var before = (await cache.GetEntryAsync(hash)).Owners;
            Assert.Single(before, p => p.StartsWith("reader-v2/", StringComparison.Ordinal));
            Assert.Single(before, p => p.StartsWith("snapshot-v2/", StringComparison.Ordinal));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            // Windows can signal process exit before asynchronous file-handle cleanup is observable.
            // Wait for actual lock release, not an arbitrary delay, before testing the nonblocking reconciliation pass.
            foreach (var owner in before.Where(p => p.StartsWith("reader-v2/", StringComparison.Ordinal) || p.StartsWith("snapshot-v2/", StringComparison.Ordinal)))
            {
                await using var released = await new FileBlobCacheLock(path + ".locks").AcquireAsync("lifetime-" + owner[(owner.IndexOf('/') + 1)..], timeout.Token);
            }
            await cache.ReconcileAsync(timeout.Token);
            var after = (await cache.GetEntryAsync(hash)).Owners;
            Assert.DoesNotContain(after, p => p.StartsWith("reader-v2/", StringComparison.Ordinal) || p.StartsWith("snapshot-v2/", StringComparison.Ordinal));
            Assert.Contains("pending/work", after);
            Assert.Single(after, p => p.StartsWith("package/", StringComparison.Ordinal));
            Assert.Equal(3, await cache.SizeAsync(hash));
            Assert.Empty(Directory.GetFiles(path + ".locks", "lifetime-*.lock"));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await SqliteStorePool.DeleteDatabaseAsync(path);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task AcknowledgedMetadataSurvivesProcessKillAndPhysicalLockIsReleased()
    {
        var directory = Directory.CreateTempSubdirectory("bsync-metadata-kill-").FullName;
        var path = Path.Combine(directory, "metadata.db");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "orchestration", path, "10000" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) Assert.Fail("Crash host exited early: " + await process.StandardError.ReadToEndAsync(timeout.Token));
                Assert.Equal($"committed {i}", line);
            }
            var locks = new FileBlobCacheLock(path + ".locks");
            using (var blocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await locks.AcquireAsync("metadata", blocked.Token));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
            await using var acquired = await locks.AcquireAsync("metadata", timeout.Token);
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, new("crash", "test", "account"));
            var cursor = int.Parse((await metadata.ReadAsync("cursor"))!.Payload, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(cursor >= 49);
            for (var i = 0; i <= cursor; i++) Assert.Equal("pending", (await metadata.ReadAsync($"jobs/{i:D6}"))!.Payload);
            Assert.Null(await metadata.ReadAsync($"jobs/{cursor + 1:D6}"));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await SqliteStorePool.DeleteDatabaseAsync(path);
            Directory.Delete(directory, true);
        }
    }
}
