using Bsync;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Transport;

internal static class MaintenanceCrashHost
{
    public static async Task RunAsync(string path, string mode)
    {
        var account = new AccountScope("crash", "test", "account");
        await using var catalog = await SqliteOrchestrationStore.OpenAsync(path + ".catalog", account);
        var accounts = new AccountComposition(catalog, new FileAccountResourceLock(path + ".lifetimes"));
        if (mode == "maintenance-checkpoint")
        {
            await new AccountMaintenance(accounts, catalog).MaintainAsync(account, "plan", [new("cleanup", "1", async (_, request, ct) =>
            {
                var key = request.Continuation is null ? "effects/one" : "effects/two";
                if (await catalog.ReadAsync(key, ct) is null) await catalog.TryWriteAsync([new(key, null, "done")], cancellationToken: ct);
                if (request.Continuation is null) return new(1, 1, "next");
                Console.WriteLine("maintenance-after-effect");
                await Task.Delay(Timeout.Infinite, ct);
                return new(1, 1, null);
            })], TimeSpan.FromMinutes(5), (_, _) => Task.FromResult(false));
            return;
        }
        await using var scope = await accounts.OpenWorkAsync(account);
        await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, account);
        await using var lease = await ExecutionLease.TryAcquireAsync(metadata, "network-owner", TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("Network lease unavailable.");
        var local = await SqliteLocalStore<CrashNote>.OpenAsync(new() { DataSource = path, Collection = "notes" }, CrashJson.Default.CrashNote);
        var engine = new SyncEngine<CrashNote>(local, new BlockingTransport(), new HybridLogicalClock("background"), DocumentCloner.Json(CrashJson.Default.CrashNote));
        await engine.WriteAsync(new() { Id = "seed", Title = "background" });
        await engine.PushAsync(lease.Lost);
    }

    private sealed class BlockingTransport : ISyncTransport<CrashNote>
    {
        public async Task<PushResult<CrashNote>> PushAsync(PushRequest<CrashNote> request, CancellationToken cancellationToken = default)
        {
            Console.WriteLine("network-held");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
        public Task<PullResult<CrashNote>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public IAsyncEnumerable<StreamEvent<CrashNote>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
