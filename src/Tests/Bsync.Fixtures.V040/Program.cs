using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Storage.Sqlite;
using Bsync.Transport;

// A replica database as Bsync 0.4.0 (published on nuget.org) leaves it, with every state an upgrade must keep:
//   pending   an unsent edit whose operation was already prepared (its operation id must be resent unchanged)
//   kept      a conflict kept for the user (Defer), with the server's and the local copy
//   rejected  an edit the server rejected (code "title-forbidden"), parked
//   g1, g2    a dependency group written offline, prepared but not sent
//   colour    a synced document with a member the reading application does not know ("colour")
if (args.Length == 2 && args[0] == "--expect-unsupported")
{
    try
    {
        await using var rejected = await Bsync.Storage.Sqlite.SqliteOrchestrationStore.OpenAsync(args[1], new Bsync.Client.AccountScope("fixtures", "published040", "alice"));
        throw new Exception("Published 0.4.0 unexpectedly opened feature version 2.");
    }
    catch (Bsync.Storage.LocalStoreUnavailableException error) when (error.Reason == "outdated")
    { Console.WriteLine("Published 0.4.0 refused feature version 2 without mutation."); return; }
}
var path = args.Length > 0 ? args[0] : "replica-0.4.0.db";
if (File.Exists(path)) throw new IOException("Capture requires a fresh destination.");

var server = new InMemorySyncServer<V040Doc>(new InMemorySyncServerOptions<V040Doc>
{
    Cloner = DocumentCloner.Json(V040Json.Default.V040Doc),
    Fingerprint = DocumentCloner.JsonFingerprint(V040Json.Default.V040Doc),
    Validator = (_, operation, _) => operation.Document.Title == "forbidden" ? "title-forbidden" : null,
});
var store = await SqliteLocalStore<V040Doc>.OpenAsync(new SqliteLocalStoreOptions { DataSource = path, Collection = "fixtures" }, V040Json.Default.V040Doc);
var transport = new Switchable(new InProcessTransport<V040Doc>(server));
var replica = new SyncEngine<V040Doc>(store, transport, new HybridLogicalClock("device-040"), DocumentCloner.Json(V040Json.Default.V040Doc));
var other = new SyncEngine<V040Doc>(
    new Bsync.Storage.InMemoryLocalStore<V040Doc>(DocumentCloner.Json(V040Json.Default.V040Doc)),
    new InProcessTransport<V040Doc>(server),
    new HybridLogicalClock("other-040"),
    DocumentCloner.Json(V040Json.Default.V040Doc));

await replica.WriteAsync(new V040Doc { Id = "colour", Title = "synced", Amount = 12.5m, Colour = "teal" });
await replica.WriteAsync(new V040Doc { Id = "kept", Title = "base" });
await replica.SyncAsync();
await other.SyncAsync();
await other.WriteAsync(new V040Doc { Id = "kept", Title = "theirs" });
await other.SyncAsync();
await replica.WriteAsync(new V040Doc { Id = "kept", Title = "mine" });
await replica.WriteAsync(new V040Doc { Id = "rejected", Title = "forbidden" });
await replica.SyncAsync(); // the conflict is kept (default policy), the rejection parked

transport.Offline = true;
await replica.WriteAsync(new V040Doc { Id = "pending", Title = "offline edit", Tags = ["a", "b"], Colour = "amber" });
await replica.WriteGroupAsync([new V040Doc { Id = "g1", Title = "order" }, new V040Doc { Id = "g2", Title = "line" }]);
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

await CaptureOrchestration.Run(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "published040"));

/// <summary>The document as the 0.4.0 application knew it: one member ("colour") later versions do not know.</summary>
public sealed class V040Doc : ISyncEntity
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
[JsonSerializable(typeof(V040Doc))]
internal sealed partial class V040Json : JsonSerializerContext;

/// <summary>A transport that can go offline.</summary>
internal sealed class Switchable(ISyncTransport<V040Doc> inner) : ISyncTransport<V040Doc>
{
    public bool Offline { get; set; }

    public Task<PullResult<V040Doc>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) =>
        Offline ? throw new SyncTransportException(SyncErrorCodes.Unavailable, "offline", isTransient: true) : inner.PullAsync(request, cancellationToken);

    public Task<PushResult<V040Doc>> PushAsync(PushRequest<V040Doc> request, CancellationToken cancellationToken = default) =>
        Offline ? throw new SyncTransportException(SyncErrorCodes.Unavailable, "offline", isTransient: true) : inner.PushAsync(request, cancellationToken);

    public IAsyncEnumerable<StreamEvent<V040Doc>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => inner.StreamAsync(since, cancellationToken);
}

