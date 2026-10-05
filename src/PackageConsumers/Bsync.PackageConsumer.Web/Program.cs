using System.Text.Json.Serialization;
using Bsync;
using Bsync.Blazor.IndexedDb;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Server.SqlServer;
using Bsync.Storage.Sqlite;
using Bsync.Transport;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// Serves one collection from the in-memory authority. With --smoke, it also starts on a free port, writes through a
// SQLite replica over HTTP, checks the server received it, and exits with 0 on success.
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
app.MapSyncCollection("items", authority, SyncJsonTypes<Item>.From(ItemJson.Default), new SyncEndpointOptions
{
    SupportedSchemas = new HashSet<string>(["items-v1"], StringComparer.Ordinal),
});

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

    // The browser store ships as a static web asset of the Bsync.Blazor package; keep the type referenced.
    Console.WriteLine($"IndexedDB store type: {typeof(IndexedDbLocalStore<Item>).FullName}");
    Console.WriteLine($"SQL Server authority type: {typeof(SqlServerSyncAuthority<Item>).FullName}");
    Console.WriteLine($"Sync complete: {result.IsComplete}; server has the item: {stored?.Title == "from a packaged SQLite replica"}");
    return result.IsComplete && stored is not null ? 0 : 1;
}
finally
{
    await app.StopAsync();
    SqliteStorePool.Release(Path.Combine(directory.FullName, "replica.db"));
    directory.Delete(recursive: true);
}

public sealed class Item : ISyncEntity
{
    public string Id { get; set; } = Guid.CreateVersion7().ToString();

    public HlcTimestamp UpdatedAt { get; set; }

    public bool Deleted { get; set; }

    public string Title { get; set; } = string.Empty;
}

[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(PullRequest))]
[JsonSerializable(typeof(PullResult<Item>))]
[JsonSerializable(typeof(PushRequest<Item>))]
[JsonSerializable(typeof(PushResult<Item>))]
public sealed partial class ItemJson : JsonSerializerContext;
