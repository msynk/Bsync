using System.Diagnostics;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Transport;
using Xunit;

namespace Bsync.Tests;

public sealed class MaintenanceProcessKillTests
{
    private static Process Start(string path, string mode)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), mode, path }) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }

    [Theory]
    [InlineData("network-owner", "network-held")]
    [InlineData("maintenance-checkpoint", "maintenance-after-effect")]
    public async Task PhysicalOwnershipAndDurableProgressSurviveProcessBoundaries(string mode, string signal)
    {
        var root = Directory.CreateTempSubdirectory("bsync-maintenance-process-").FullName;
        var path = Path.Combine(root, "state.db");
        using var process = Start(path, mode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) Assert.Fail("Crash host exited early: " + await process.StandardError.ReadToEndAsync(token));
            Assert.Equal(signal, line);
            var account = new AccountScope("crash", "test", "account");
            await using var catalog = await SqliteOrchestrationStore.OpenAsync(path + ".catalog", account, cancellationToken: token);
            var locks = new FileAccountResourceLock(path + ".lifetimes");
            var accounts = new AccountComposition(catalog, locks);
            var runner = new AccountMaintenance(accounts, catalog);
            AccountMaintenanceStep[] steps = [new("cleanup", "1", async (_, request, ct) =>
            {
                if (mode == "maintenance-checkpoint")
                {
                    Assert.Equal("next", request.Continuation);
                    Assert.Equal("done", (await catalog.ReadAsync("effects/two", ct))!.Payload);
                }
                return new(1, 1, null);
            })];
            Assert.Equal(MaintenanceOutcome.Busy, (await runner.MaintainAsync(account, "plan", steps, TimeSpan.FromSeconds(5), (_, _) => Task.FromResult(false), cancellationToken: token)).Outcome);
            await using (var coordinator = accounts.CreateCoordinator())
                await Assert.ThrowsAsync<AccountResourcesInUseException>(() => coordinator.DeleteAccountAsync(account, cancellationToken: token));
            if (mode == "network-owner")
            {
                await using var foreground = await accounts.OpenWorkAsync(account, token);
                await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, account, cancellationToken: token);
                Assert.Null(await ExecutionLease.TryAcquireAsync(metadata, "network-owner", TimeSpan.FromSeconds(30), cancellationToken: token));
                var local = await SqliteLocalStore<CrashNote>.OpenAsync(new() { DataSource = path, Collection = "notes" }, CrashJson.Default.CrashNote, token);
                var engine = new SyncEngine<CrashNote>(local, new NoNetwork(), new HybridLogicalClock("foreground"), DocumentCloner.Json(CrashJson.Default.CrashNote));
                Assert.Equal("background", (await engine.GetAsync("seed", token))!.Current.Title);
                await engine.WriteAsync(new() { Id = "foreground", Title = "local write while network blocked" }, token);
                Assert.True((await engine.GetAsync("foreground", token))!.IsDirty);
            }
            else await Assert.ThrowsAsync<AccountResourcesInUseException>(() => accounts.OpenWorkAsync(account, token));
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync(token);
            AccountMaintenanceResult result;
            do
            {
                result = await runner.MaintainAsync(account, "plan", steps, TimeSpan.FromSeconds(5), (_, _) => Task.FromResult(false), cancellationToken: token);
                if (result.Outcome == MaintenanceOutcome.Busy) await Task.Delay(25, token); // Actual OS handle cleanup can lag process exit.
            } while (result.Outcome == MaintenanceOutcome.Busy);
            Assert.Equal(MaintenanceOutcome.Completed, result.Outcome);
            if (mode == "maintenance-checkpoint") Assert.Equal(2, (await catalog.ReadPageAsync("effects/", null, 10, token)).Count);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await SqliteStorePool.DeleteDatabaseAsync(path); await SqliteStorePool.DeleteDatabaseAsync(path + ".catalog");
            Directory.Delete(root, true);
        }
    }

    private sealed class NoNetwork : ISyncTransport<CrashNote>
    {
        public Task<PushResult<CrashNote>> PushAsync(PushRequest<CrashNote> request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<PullResult<CrashNote>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public IAsyncEnumerable<StreamEvent<CrashNote>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
