using System.Globalization;
using Bsync;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

// Usage:
//   writes <db> <count>          local writes through SyncEngine; prints "committed <i> <timestamp>" after each receipt
//   pages  <db> <count> <size>   atomic page commits (records + cursor); prints "committed <i>" after each commit
// The test process kills this process at an arbitrary point and inspects the database afterwards.

var mode = args[0];
if (mode == "workflow")
{
    var account = new Bsync.Client.AccountScope("crash", "test", "account");
    await using var metadata = await SqliteOrchestrationStore.OpenAsync(args[1], account);
    var stopAfterEffect = int.Parse(args[2], CultureInfo.InvariantCulture);
    var steps = Enumerable.Range(0, 2).Select(index => new Bsync.Client.WorkflowStep("step-" + index, async (operation, ct) =>
    {
        // Simulated receiver: effect and its operation identity commit before returning to workflow bookkeeping.
        var key = "effects/" + index;
        if (await metadata.ReadAsync(key, ct) is null && !await metadata.TryWriteAsync([new(key, null, operation)], cancellationToken: ct))
            throw new InvalidOperationException("Concurrent effect write.");
        if (index == stopAfterEffect)
        {
            Console.WriteLine("effect " + index);
            await Task.Delay(Timeout.Infinite, ct);
        }
        return Bsync.Client.WorkflowStepOutcome.Complete;
    })).ToArray();
    await new Bsync.Client.RecoverableWorkflow(metadata, account).RunAsync("work", "1", steps, TimeSpan.FromMinutes(5));
    return;
}
if (mode == "account-resource")
{
    var locks = new Bsync.Client.FileAccountResourceLock(args[1]);
    await using var held = await locks.TryAcquireAsync(new("crash", "test", "account"), exclusive: false)
        ?? throw new InvalidOperationException("Cannot acquire account resources.");
    Console.WriteLine("account-open");
    await Task.Delay(Timeout.Infinite);
    return;
}
if (mode == "managed-reader")
{
    await using var metadata = await SqliteOrchestrationStore.OpenAsync(args[1], new("crash", "test", "account"));
    var locks = new Bsync.Blobs.FileBlobCacheLock(args[1] + ".locks");
    var cache = new Bsync.Blobs.ManagedBlobCache(new Bsync.Blobs.FileBlobCache(args[1] + ".blobs"), metadata, locks, 100, 100);
    var blob = await cache.ImportPinnedAsync(new MemoryStream([1, 2, 3]), "pending/work");
    var transfers = new Bsync.Blobs.BlobTransferManager(metadata, cache, new(new HttpClient { BaseAddress = new Uri("https://unused.invalid/") }));
    var packages = new Bsync.Blobs.OfflinePackageManager(metadata, cache, transfers, locks, (_, _) => Task.FromResult(true));
    await packages.StageAsync(new(1, "manual", "one", null, [new("page", blob.Sha256, blob.Size, true, [])]));
    if (!await packages.TryActivateAsync("manual", "one")) throw new InvalidOperationException("Cannot activate crash-host package.");
    await using var snapshot = await packages.OpenActiveAsync("manual");
    await using var reader = await snapshot.OpenAsync("page");
    Console.WriteLine("reader " + blob.Sha256);
    await Task.Delay(Timeout.Infinite);
    return;
}
if (mode == "orchestration")
{
    var locks = new Bsync.Blobs.FileBlobCacheLock(args[1] + ".locks");
    await using var held = await locks.AcquireAsync("metadata");
    await using var metadata = await SqliteOrchestrationStore.OpenAsync(args[1], new("crash", "test", "account"));
    for (var i = 0; i < int.Parse(args[2], CultureInfo.InvariantCulture); i++)
    {
        var cursor = await metadata.ReadAsync("cursor");
        if (!await metadata.TryWriteAsync([new($"jobs/{i:D6}", null, "pending"), new("cursor", cursor?.Revision, i.ToString(CultureInfo.InvariantCulture))]))
            throw new InvalidOperationException("Unexpected concurrent crash-host writer.");
        Console.WriteLine($"committed {i}");
    }
    await Task.Delay(Timeout.Infinite);
    return;
}
var store = await SqliteLocalStore<CrashNote>.OpenAsync(
    new SqliteLocalStoreOptions { DataSource = args[1], Collection = "notes" },
    CrashJson.Default.CrashNote);
var count = int.Parse(args[2], CultureInfo.InvariantCulture);

switch (mode)
{
    case "writes":
        var engine = new SyncEngine<CrashNote>(store, new NoNetwork(), new HybridLogicalClock("crash-host"), DocumentCloner.Json(CrashJson.Default.CrashNote));
        for (var i = 0; i < count; i++)
        {
            var receipt = await engine.WriteAsync(new CrashNote { Id = $"w{i:D6}", Title = new string('x', 200) });
            Console.WriteLine($"committed {i} {receipt.UpdatedAt}");
        }

        break;

    case "pages":
        var size = int.Parse(args[3], CultureInfo.InvariantCulture);
        for (var i = 0; i < count; i++)
        {
            var batch = i;
            var updates = Enumerable.Range(0, size)
                .Select(j => new RecordUpdate<CrashNote>(
                    $"b{batch:D6}-{j:D3}",
                    _ => new SyncRecord<CrashNote>(new CrashNote { Id = $"b{batch:D6}-{j:D3}", Title = new string('y', 200) }, null, IsDirty: false) { BaseVersion = batch + 1 }))
                .ToList();
            await store.UpdateAsync(updates, new ReplicaCursor(new Checkpoint($"cp{batch}"), 0, false));
            Console.WriteLine($"committed {i}");
        }

        break;

    default:
        throw new ArgumentException($"Unknown mode '{mode}'.");
}
