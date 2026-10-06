using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Running;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Conflicts;
using Bsync.Documents;
using Bsync.Server;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

if (args is ["peak", var file, var countText])
{
    // Task E1, peak memory: "peak <file> <count>" seeds a SQLite file in this process, then measures a fresh process
    // ("peak-query <file>") that opens it and reads one ordered 50-item page, so seeding does not count.
    var count = int.Parse(countText, System.Globalization.CultureInfo.InvariantCulture);
    var seeded = await SqliteLocalStore<BenchDocument>.OpenAsync(new SqliteLocalStoreOptions { DataSource = file }, BenchJson.Default.BenchDocument);
    await Workload.SeedSyncedAsync(seeded, count, "seed");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [typeof(Workload).Assembly.Location, "peak-query", file]) { RedirectStandardOutput = true })!;
    Console.WriteLine(await child.StandardOutput.ReadToEndAsync());
    await child.WaitForExitAsync();
    return;
}

if (args is ["peak-query", var path])
{
    var store = await SqliteLocalStore<BenchDocument>.OpenAsync(new SqliteLocalStoreOptions { DataSource = path }, BenchJson.Default.BenchDocument);
    var opened = Process.GetCurrentProcess().PeakWorkingSet64;
    var timer = Stopwatch.StartNew();
    var all = await store.QueryAsync();
    var page = new SyncQuery<BenchDocument> { Order = (a, b) => b.Due.CompareTo(a.Due), Limit = 50 }.Apply(all);
    timer.Stop();
    var after = Process.GetCurrentProcess();
    after.Refresh();
    Console.WriteLine($"documents={all.Count} page={page.Count} elapsedMs={timer.ElapsedMilliseconds} peakWorkingSetAfterOpen={opened / 1048576.0:0.0}MiB peakWorkingSetAfterQuery={after.PeakWorkingSet64 / 1048576.0:0.0}MiB");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Workload).Assembly).Run(args);
