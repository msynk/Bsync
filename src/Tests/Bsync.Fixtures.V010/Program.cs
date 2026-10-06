using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Storage.Sqlite;
using Bsync.Transport;

// A replica database as Bsync 0.1.0 (published on nuget.org) leaves it, with every state an upgrade must keep:
//   pending   an unsent edit whose operation was already prepared (its operation id must be resent unchanged)
//   kept      a conflict kept for the user (Defer), with the server's and the local copy
//   rejected  an edit the server rejected (code "title-forbidden"), parked
//   g1, g2    a dependency group written offline, prepared but not sent
//   colour    a synced document with a member the reading application does not know ("colour")
var path = args.Length > 0 ? args[0] : "replica-0.1.0.db";
File.Delete(path);

var server = new InMemorySyncServer<V010Doc>(new InMemorySyncServerOptions<V010Doc>
{
    Cloner = DocumentCloner.Json(V010Json.Default.V010Doc),
    Fingerprint = DocumentCloner.JsonFingerprint(V010Json.Default.V010Doc),
    Validator = (operation, _) => operation.Document.Title == "forbidden" ? "title-forbidden" : null,
});
var store = await SqliteLocalStore<V010Doc>.OpenAsync(new SqliteLocalStoreOptions { DataSource = path, Collection = "fixtures" }, V010Json.Default.V010Doc);
var transport = new Switchable(new InProcessTransport<V010Doc>(server));
var replica = new SyncEngine<V010Doc>(store, transport, new HybridLogicalClock("device-010"), DocumentCloner.Json(V010Json.Default.V010Doc));
var other = new SyncEngine<V010Doc>(
    new Bsync.Storage.InMemoryLocalStore<V010Doc>(DocumentCloner.Json(V010Json.Default.V010Doc)),
    new InProcessTransport<V010Doc>(server),
    new HybridLogicalClock("other-010"),
    DocumentCloner.Json(V010Json.Default.V010Doc));

await replica.WriteAsync(new V010Doc { Id = "colour", Title = "synced", Amount = 12.5m, Colour = "teal" });
await replica.WriteAsync(new V010Doc { Id = "kept", Title = "base" });
await replica.SyncAsync();
await other.SyncAsync();
await other.WriteAsync(new V010Doc { Id = "kept", Title = "theirs" });
await other.SyncAsync();
await replica.WriteAsync(new V010Doc { Id = "kept", Title = "mine" });
await replica.WriteAsync(new V010Doc { Id = "rejected", Title = "forbidden" });
await replica.SyncAsync(); // the conflict is kept (default policy), the rejection parked

transport.Offline = true;
await replica.WriteAsync(new V010Doc { Id = "pending", Title = "offline edit", Tags = ["a", "b"], Colour = "amber" });
await replica.WriteGroupAsync([new V010Doc { Id = "g1", Title = "order" }, new V010Doc { Id = "g2", Title = "line" }]);
try
{
    await replica.PushAsync(); // prepares the operations, then fails to send them
}
catch (SyncTransportException)
{
}

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
foreach (var id in new[] { "colour", "kept", "rejected", "pending", "g1", "g2" })
{
    var record = await store.GetAsync(id);
    Console.WriteLine($"{id}: dirty={record!.IsDirty} pending={record.Pending?.OperationId} conflict={record.Conflict is not null} rejection={record.Rejection?.ErrorCode} group={record.Group?.Id}");
}

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
Console.WriteLine($"Wrote {Path.GetFullPath(path)}");

/// <summary>The document as the 0.1.0 application knew it: one member ("colour") later versions do not know.</summary>
public sealed class V010Doc : ISyncEntity
{
    public string Id { get; set; } = string.Empty;

    public HlcTimestamp UpdatedAt { get; set; }

    public bool Deleted { get; set; }

    public string? Title { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset? Due { get; set; }

    public List<string> Tags { get; set; } = [];

    public string? Colour { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(V010Doc))]
internal sealed partial class V010Json : JsonSerializerContext;

/// <summary>A transport that can go offline.</summary>
internal sealed class Switchable(ISyncTransport<V010Doc> inner) : ISyncTransport<V010Doc>
{
    public bool Offline { get; set; }

    public Task<PullResult<V010Doc>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) =>
        Offline ? throw new SyncTransportException(SyncErrorCodes.Unavailable, "offline", isTransient: true) : inner.PullAsync(request, cancellationToken);

    public Task<PushResult<V010Doc>> PushAsync(PushRequest<V010Doc> request, CancellationToken cancellationToken = default) =>
        Offline ? throw new SyncTransportException(SyncErrorCodes.Unavailable, "offline", isTransient: true) : inner.PushAsync(request, cancellationToken);

    public IAsyncEnumerable<StreamEvent<V010Doc>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => inner.StreamAsync(since, cancellationToken);
}
