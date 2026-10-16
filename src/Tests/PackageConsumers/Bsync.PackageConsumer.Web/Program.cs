using System.Text.Json.Serialization;
using Bsync;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Bsync.Clocks;
using Bsync.Client;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Server.AspNetCore.Blobs;
using Bsync.Server.SqlServer;
using Bsync.Storage.Sqlite;
using Bsync.Transport;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// Serves one collection from the in-memory authority, and blobs from a directory. With --smoke, it also starts on a free
// port, writes through a SQLite replica over HTTP, uploads and downloads a blob, checks the server received both, and
// exits with 0 on success.
var smoke = args.Contains("--smoke");
var builder = WebApplication.CreateSlimBuilder(args);
if (smoke)
{
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

var app = builder.Build();
var authority = new InMemorySyncServer<Item>(new InMemorySyncServerOptions<Item>
{
    Cloner = DocumentCloner.Json(ItemJson.Default.Item),
    Fingerprint = DocumentCloner.JsonFingerprint(ItemJson.Default.Item),
});
var syncOptions = new SyncEndpointOptions
{
    SupportedSchemas = new HashSet<string>(["items-v1"], StringComparer.Ordinal),
};
app.MapSyncCollection("items", authority, SyncJsonTypes<Item>.From(ItemJson.Default), syncOptions);
var blobDirectory = Directory.CreateTempSubdirectory("bsync-consumer-blobs-");
var blobs = new FileSystemBlobStore(blobDirectory.FullName);
app.MapSyncBlobs(blobs, new OpenBlobAccess(), syncOptions);

if (!smoke)
{
    app.Run();
    return 0;
}

await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
var directory = Directory.CreateTempSubdirectory("bsync-consumer-");
try
{
    var store = await SqliteLocalStore<Item>.OpenAsync(
        new SqliteLocalStoreOptions { DataSource = Path.Combine(directory.FullName, "replica.db"), Collection = "items" },
        ItemJson.Default.Item);
    var identity = await store.GetReplicaIdentityAsync();
    using var http = new HttpClient { BaseAddress = new Uri(address + "/") };
    var transport = new HttpSyncTransport<Item>(http, new HttpSyncTransportOptions { Collection = "items", SchemaId = "items-v1" }, SyncJsonTypes<Item>.From(ItemJson.Default));
    var engine = new SyncEngine<Item>(store, transport, new HybridLogicalClock(identity.Incarnation), DocumentCloner.Json(ItemJson.Default.Item));

    await engine.WriteAsync(new Item { Id = "smoke", Title = "from a packaged SQLite replica" });
    var result = await engine.SyncAsync();
    var stored = authority.Snapshot().SingleOrDefault(i => i.Id == "smoke");

    var device = new FileBlobCache(Path.Combine(directory.FullName, "device-blobs"));
    var (sha256, size) = await device.ImportAsync(new MemoryStream("attachment from the packages"u8.ToArray()));
    var transfer = new HttpBlobTransfer(http);
    await transfer.UploadAsync(device, sha256);
    var other = new FileBlobCache(Path.Combine(directory.FullName, "other-blobs"));
    var received = await transfer.DownloadAsync(other, sha256, size);
    Console.WriteLine($"Blob uploaded and downloaded: {received == size && await blobs.ExistsAsync(sha256)}");

    var account = new AccountScope("consumer", "smoke", "account");
    if (!new AccountPendingWork(0, 0, 0) { UnknownResources = 1 }.HasWork)
        throw new InvalidOperationException("Unknown resource work must prevent default deletion.");
    var accountLocks = new FileAccountResourceLock(Path.Combine(directory.FullName, "account-locks"));
    await using (var localAccess = await accountLocks.TryAcquireAsync(account, exclusive: false))
    {
        if (localAccess is null || await accountLocks.TryAcquireAsync(account, exclusive: true) is not null)
            throw new InvalidOperationException("Account resource lock did not protect a local reader.");
    }
    using (var keyAccess = LocalKeyAccess.Available(new(account, "replica", 1), new byte[32]))
        if (keyAccess.Key.Length != 32) throw new InvalidOperationException("Local key contract failed.");
    var intentDefinition = new IntentDefinition<SmokeIntentPayload>("acknowledge", 1, ItemJson.Default.SmokeIntentPayload);
    var intent = intentDefinition.Create("command", "document", "r1", new("r1"));
    var intentCopy = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(intent, ItemJson.Default.IntentRequest), ItemJson.Default.IntentRequest)!;
    if (intentDefinition.ReadPayload(intentCopy).Revision != "r1") throw new InvalidOperationException("Intent registration roundtrip failed.");
    await using var metadata = await SqliteOrchestrationStore.OpenAsync(Path.Combine(directory.FullName, "metadata.db"), account);
    var locks = new FileBlobCacheLock(Path.Combine(directory.FullName, "locks"));
    var managed = new ManagedBlobCache(other, metadata, locks, 1024 * 1024, 2 * 1024 * 1024);
    await managed.ReconcileAsync();
    var jobs = new BlobTransferManager(metadata, managed, transfer);
    var packages = new OfflinePackageManager(metadata, managed, jobs, locks, (_, _) => Task.FromResult(true));
    await packages.StageAsync(new(1, "manual", "one", null, [new("page", sha256, size, true, [])]));
    var ready = await packages.PrepareAsync("manual", TimeSpan.FromSeconds(10));
    if ((await packages.GetLastVerificationAsync("manual"))?.ActiveRevision != "one") throw new InvalidOperationException("Package verification metadata failed.");
    using (var metrics = new Bsync.Diagnostics.OfflineMetrics(metadata)) await metrics.RefreshAsync();
    await using (var snapshot = await packages.OpenActiveAsync("manual"))
    await using (var page = await snapshot.OpenAsync("page"))
        if (page?.Length != size) throw new InvalidOperationException("Packaged snapshot failed.");
    var workflow = await new RecoverableWorkflow(metadata, account).RunAsync("smoke", "1",
        [new("verify", (_, _) => Task.FromResult(WorkflowStepOutcome.Complete))], TimeSpan.FromSeconds(5));
    Console.WriteLine($"Package ready: {ready.Readiness}; workflow: {workflow?.State}");

    var readModel = new LocalReadModel(metadata, "items-summary");
    // The single-threaded smoke host has no concurrent source writers; capture before enumerating.
    var captured = authority.Snapshot().Select(item => new ReadModelRow(item.Id, item.Title)).ToArray();
    async IAsyncEnumerable<ReadModelRow> Project()
    {
        foreach (var row in captured) yield return row;
        await Task.CompletedTask;
    }
    if (!await readModel.RebuildAsync("1", "smoke-capture-1", Project(), TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Read model did not activate.");
    var projected = (await (await readModel.OpenActiveAsync())!.ReadPageAsync(10)).Rows;
    if (projected.Single().Id != "smoke") throw new InvalidOperationException("Packaged read model failed.");
    var diagnostic = await Bsync.Diagnostics.OfflineDiagnosticExport.ExportJsonAsync(metadata);
    if (diagnostic.Contains(sha256, StringComparison.Ordinal)) throw new InvalidOperationException("Diagnostic export leaked content identity.");

    // The browser store ships as a static web asset of the Bsync.Blazor package; keep the type referenced.
    Console.WriteLine($"IndexedDB store type: {typeof(IndexedDbLocalStore<Item>).FullName}");
    Console.WriteLine($"SQL Server authority type: {typeof(SqlServerSyncAuthority<Item>).FullName}");
    Console.WriteLine($"Sync complete: {result.IsComplete}; server has the item: {stored?.Title == "from a packaged SQLite replica"}");
    return result.IsComplete && stored is not null && received == size && ready.Readiness == OfflinePackageReadiness.ReadyFreshnessUnknown && workflow?.State == "complete" ? 0 : 1;
}
finally
{
    await app.StopAsync();
    blobDirectory.Delete(recursive: true);
    SqliteStorePool.Release(Path.Combine(directory.FullName, "replica.db"));
    SqliteStorePool.Release(Path.Combine(directory.FullName, "metadata.db"));
    directory.Delete(recursive: true);
}

/// <summary>Every caller holds and may read every blob (a smoke test; real apps check their own tables).</summary>
public sealed class OpenBlobAccess : IBlobAccess
{
    public Task<bool> HoldsAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task RecordAsync(SyncCallContext caller, string sha256, long size, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> CanReadAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default) => Task.FromResult(true);
}

public sealed class Item : ISyncEntity
{
    public string Id { get; set; } = Guid.CreateVersion7().ToString();

    public HlcTimestamp UpdatedAt { get; set; }

    public bool Deleted { get; set; }

    public string Title { get; set; } = string.Empty;
}

[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(SmokeIntentPayload))]
[JsonSerializable(typeof(IntentRequest))]
[JsonSerializable(typeof(PullRequest))]
[JsonSerializable(typeof(PullResult<Item>))]
[JsonSerializable(typeof(PushRequest<Item>))]
[JsonSerializable(typeof(PushResult<Item>))]
public sealed partial class ItemJson : JsonSerializerContext;

public sealed record SmokeIntentPayload(string Revision);
