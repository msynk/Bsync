using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

namespace Bsync.Samples.NativeBackground;

/// <summary>
/// Minimal UI-independent reference host. Its example workload atomically turns bounded durable local requests into
/// results. Replace that workload with application sync/transfer work; it is not a demonstration of remote delivery.
/// Account registration is explicit. Background callbacks never create an account registration or replacement key.
/// </summary>
public sealed class HeadlessSampleHost(string directory)
{
    public static readonly AccountScope DemoAccount = new("Bsync.HeadlessSample", "local", "demo");
    private static readonly AccountScope CatalogAccount = new("Bsync.HeadlessSample", "local", "catalog");
    private string CatalogPath => Path.Combine(directory, "catalog.db");
    private FileAccountResourceLock Locks => new(Path.Combine(directory, "account-locks"));
    private Task<SqliteOrchestrationStore> Catalog(CancellationToken token) => SqliteOrchestrationStore.OpenAsync(CatalogPath, CatalogAccount, cancellationToken: token);

    /// <summary>Call from Application/AppDelegate bootstrap on every process launch, before scheduler callbacks.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(directory);
        NativeBackgroundRuntime.Current = new(OpenAsync, AccountsAsync);
    }
    /// <summary>Explicit foreground provisioning, followed by durable work and a best-effort scheduler request.</summary>
    public async Task<BackgroundWorkOutcome> RequestDemoAsync(IBackgroundWorkScheduler scheduler, CancellationToken cancellationToken = default)
    {
        await using var catalog = await Catalog(cancellationToken);
        var key = "registered/" + DemoAccount.StorageKey;
        await using var access = await Locks.TryAcquireAsync(DemoAccount, false, cancellationToken) ?? throw new AccountResourcesInUseException();
        if (await catalog.ReadAsync("deleting/" + DemoAccount.StorageKey, cancellationToken) is not null) return BackgroundWorkOutcome.DeferredByHost;
        var registration = await catalog.ReadAsync(key, cancellationToken);
        if (registration is null) await catalog.TryWriteAsync([new(key, null, "1")], cancellationToken: cancellationToken);
        await catalog.TryWriteAsync([new("demo-work/" + DemoAccount.StorageKey + "/" + Guid.NewGuid().ToString("N"), null, "queued")], cancellationToken: cancellationToken);
        return await new BackgroundWorkDispatcher(catalog).RequestAsync(new(DemoAccount, 0, new(RequiresNetwork: false)), scheduler, cancellationToken);
    }
    private async Task<IReadOnlyList<string>> AccountsAsync(CancellationToken token)
    {
        await using var catalog = await Catalog(token);
        var accounts = await catalog.ReadPageAsync("registered/", null, 101, token);
        if (accounts.Count > 100) throw new InvalidDataException("Reference host supports at most 100 registrations.");
        return accounts.Where(r => r.Payload == "1").Select(r => r.Key["registered/".Length..]).ToArray();
    }
    private async Task<IBackgroundAccountScope?> OpenAsync(string storageKey, CancellationToken token)
    {
        // This minimal host provisions only its declared demo identity, never arbitrary callback data.
        if (storageKey != DemoAccount.StorageKey) return null;
        var catalog = await Catalog(token);
        IAsyncDisposable? access = null;
        try
        {
            access = await Locks.TryAcquireAsync(DemoAccount, false, token);
            if (access is null || await catalog.ReadAsync("registered/" + storageKey, token) is not { Payload: "1" } ||
                await catalog.ReadAsync("deleting/" + storageKey, token) is not null)
            { await catalog.DisposeAsync(); if (access is not null) await access.DisposeAsync(); return null; }
            var dispatcher = new BackgroundWorkDispatcher(catalog);
            // A crash after local enqueue but before scheduler request must still be found on foreground/relaunch.
            if ((await catalog.ReadPageAsync("demo-work/" + storageKey + "/", null, 1, token)).Count > 0)
                await dispatcher.RequestAsync(new(DemoAccount, 0, new(false)), new DeferredScheduler(), token);
            return new Scope(catalog, access, dispatcher);
        }
        catch { await catalog.DisposeAsync(); if (access is not null) await access.DisposeAsync(); throw; }
    }
    private sealed class DeferredScheduler : IBackgroundWorkScheduler
    {
        public BackgroundCapabilities Capabilities { get; } = new(true, true, true);
        public Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default) => Task.FromResult(BackgroundWorkOutcome.DeferredByHost);
    }
    private sealed class Scope(SqliteOrchestrationStore catalog, IAsyncDisposable access, BackgroundWorkDispatcher dispatcher) : IBackgroundAccountScope
    {
        public AccountScope Account => DemoAccount;
        public BackgroundWorkDispatcher Dispatcher => dispatcher;
        public async Task<BackgroundWorkOutcome> ExecuteAsync(CancellationToken cancellationToken)
        {
            var rows = await catalog.ReadPageAsync("demo-work/" + Account.StorageKey + "/", null, 32, cancellationToken);
            foreach (var row in rows)
            {
                var resultKey = "demo-result/" + row.Key["demo-work/".Length..];
                var result = await catalog.ReadAsync(resultKey, cancellationToken);
                if (!await catalog.TryWriteAsync([new(row.Key, row.Revision, null), new(resultKey, result?.Revision, "completed")], cancellationToken: cancellationToken))
                    return BackgroundWorkOutcome.RemainingWork;
            }
            return rows.Count < 32 ? BackgroundWorkOutcome.Completed : BackgroundWorkOutcome.RemainingWork;
        }
        public async ValueTask DisposeAsync() { await catalog.DisposeAsync(); await access.DisposeAsync(); }
    }
}
