using System.Security.Cryptography;
using Bsync.Blazor.Blobs;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Bsync.Client;

namespace Bsync.Tests.BrowserHost;

public static partial class Harness
{
    private static async Task MaintenanceChecks(string name, bool reopened)
    {
        var account = new AccountScope("tests", "browser-maintenance", name);
        await using var store = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        await using var locks = await BrowserBlobCacheLock.OpenAsync(_js, account);
        await using var lifetimes = await BrowserAccountResourceLock.OpenAsync(_js);
        var backing = await BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var cache = new ManagedBlobCache(backing, store, locks, 10000, 10000);
        using var http = new HttpClient { BaseAddress = new("https://maintenance.invalid/") };
        var transfers = new BlobTransferManager(store, cache, new(http));
        var packages = new OfflinePackageManager(store, cache, transfers, locks, (_, _) => Task.FromResult(true));
        var composition = new AccountComposition(store, lifetimes);
        var runner = new AccountMaintenance(composition, store);
        var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
        AccountMaintenanceStep[] steps = [
            new("packages", "1", (scope, request, ct) => packages.ReconcilePinsAsync(scope, request, ct)),
            new("transfers", "1", (scope, request, ct) => transfers.ReconcilePinsAsync(scope, request, ct)),
            new("owners", "1", async (scope, request, ct) => (await cache.ReconcileBuiltInOwnersAsync(scope, request, ct)).Progress),
        ];
        if (!reopened)
        {
            await using (var work = await composition.OpenWorkAsync(account))
            {
                await cache.ImportAsync(new MemoryStream([1, 2, 3]));
                if (await BlobVerification.VerifyAsync(cache, hash, 3, TimeSpan.FromSeconds(10), 2) != BlobVerificationResult.ContentLimitExceeded ||
                    await BlobVerification.VerifyAsync(cache, hash, 3, TimeSpan.FromSeconds(10), 3) != BlobVerificationResult.Verified)
                    throw new Exception("Browser verification byte allowance failed.");
                await packages.StageAsync(new(1, "book", "one", null, [new("page", hash, 3, true, [])]));
                foreach (var owner in (await cache.GetEntryAsync(hash)).Owners) await cache.UnpinAsync(hash, owner);
                var busy = await runner.MaintainAsync(account, "plan", steps, TimeSpan.FromSeconds(10), (_, _) => Task.FromResult(false));
                if (busy.Outcome != MaintenanceOutcome.Busy) throw new Exception("Maintenance bypassed a live browser account.");
            }
            await runner.MaintainAsync(account, "plan", steps, TimeSpan.FromSeconds(10), (_, _) => Task.FromResult(false), maximumItems: 1, dryRun: true);
            if ((await cache.GetEntryAsync(hash)).Owners.Length != 0 || (await store.ReadPageAsync("maintenance/", null, 10)).Count != 0)
                throw new Exception("Dry run changed browser pins or progress.");
            var progress = await runner.MaintainAsync(account, "plan", steps, TimeSpan.FromSeconds(10), (_, _) => Task.FromResult(false), maximumItems: 1);
            if (progress.Outcome != MaintenanceOutcome.RemainingWork || (await store.ReadPageAsync("maintenance/", null, 10)).Count != 1)
                throw new Exception("Browser maintenance failed to persist progress.");
            return;
        }
        var completed = false;
        for (var i = 0; i < 20; i++)
        {
            var progress = await runner.MaintainAsync(account, "plan", steps, TimeSpan.FromSeconds(10), (_, _) => Task.FromResult(false), maximumItems: 1);
            if (progress.Outcome == MaintenanceOutcome.Completed) { completed = true; break; }
            if (progress.Outcome != MaintenanceOutcome.RemainingWork) throw new Exception($"Unexpected browser maintenance outcome: {progress.Outcome}; items={progress.Items}, pages={progress.Pages}.");
        }
        if (!completed || (await cache.GetEntryAsync(hash)).Owners.Length != 2) throw new Exception("Browser restart failed to restore durable owners.");
        await store.DeleteAllAsync();
        await BrowserBlobStore.DeleteAllAsync(_js, account.StorageKey);
    }
}
