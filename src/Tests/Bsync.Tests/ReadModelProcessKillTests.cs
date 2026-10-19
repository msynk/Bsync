using System.Diagnostics;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class ReadModelProcessKillTests
{
    [Fact]
    public async Task AnotherProcessReaderProtectsRetiredRowsUntilActualProcessDeath()
    {
        var root = Directory.CreateTempSubdirectory("bsync-readmodel-kill-").FullName;
        var path = Path.Combine(root, "metadata.db");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "read-model-reader", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) Assert.Fail("Crash host exited early: " + await process.StandardError.ReadToEndAsync(timeout.Token));
            Assert.Equal("reader-held", line);
            var account = new AccountScope("crash", "test", "account");
            await using var store = await SqliteOrchestrationStore.OpenAsync(path, account, cancellationToken: timeout.Token);
            var model = new LocalReadModel(store, "tasks", account, new FileAccountResourceLock(path + ".lifetimes"));
            var protectedPage = await model.CollectRetiredAsync(rowLimit: 17, cancellationToken: timeout.Token);
            Assert.Equal(1, protectedPage.ProtectedGenerations);
            Assert.Equal(0, protectedPage.RemovedRows);
            Assert.True(await store.TryWriteAsync([new OrchestrationMutation("foreground/local-work", null, "pending")], cancellationToken: timeout.Token));
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
            var removed = 0;
            while (removed < 130)
            {
                var page = await model.CollectRetiredAsync(rowLimit: 17, cancellationToken: timeout.Token);
                Assert.InRange(page.RemovedRows, 0, 17); removed += page.RemovedRows;
                // Collection probes physical ownership without waiting. A fixed number of immediate
                // probes is not a progress guarantee, even after the former reader process has exited.
                // Keep the original deadline and require every retired row to be reclaimed.
                if (page.RemovedRows == 0) await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(130, removed);
            Assert.Equal("pending", (await store.ReadAsync("foreground/local-work", timeout.Token))!.Payload);
            await using var active = (await model.OpenActiveAsync(timeout.Token))!;
            Assert.Single((await active.ReadPageAsync(10, cancellationToken: timeout.Token)).Rows);
            Assert.Equal("new", (await model.GetStatusAsync(timeout.Token)).Active!.SourceVersion);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await SqliteStorePool.DeleteDatabaseAsync(path); Directory.Delete(root, true);
        }
    }
}
