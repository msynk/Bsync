using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class IntentRequestTests
{
    private static readonly IntentDefinition<CompleteWork> Complete = new("complete-work", 1, IntentTestJson.Default.CompleteWork);
    private static readonly IntentDefinition<AcknowledgeRevision> Acknowledge = new("acknowledge-revision", 2, IntentTestJson.Default.AcknowledgeRevision);

    [Fact]
    public void MutableInputsAreSnapshotsAndOnlyExplicitPayloadRegistrationsDecode()
    {
        var payload = new CompleteWork { Note = "first" };
        var dependencies = new[] { "attachment/one" };
        var request = Complete.Create("command", "work", "r3", payload, dependencies);
        payload.Note = "mutated"; dependencies[0] = "mutated";
        Assert.Equal("first", Complete.ReadPayload(request).Note);
        Assert.Equal("attachment/one", Assert.Single(request.Prerequisites));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)request.Prerequisites)[0] = "changed");
        Assert.Throws<InvalidOperationException>(() => Acknowledge.ReadPayload(request));
        var newer = new IntentRequest("command", Complete.Kind, 2, "work", "r3", request.PayloadJson);
        Assert.False(Complete.Matches(newer));
        Assert.Throws<InvalidOperationException>(() => Complete.ReadPayload(newer));
    }

    [Fact]
    public void BoundedRequestsRejectMalformedOrAmbiguousIdentities()
    {
        Assert.Throws<ArgumentException>(() => new IntentRequest("c", "kind", 1, "t", null, "[]"));
        Assert.Throws<ArgumentException>(() => new IntentRequest("c", "kind", 1, "t", null, "{\"value\":\"" + new string('x', 65536) + "\"}"));
        Assert.Throws<ArgumentException>(() => new IntentRequest("c", "kind", 1, "t", null, "{}", ["x", "x"]));
        Assert.Throws<ArgumentException>(() => new IntentRequest("c", "System.Type, Assembly", 1, "t", null, "{}"));
        Assert.Throws<System.Text.EncoderFallbackException>(() => new IntentRequest("\ud800", "kind", 1, "t", null, "{}"));
    }

    [Fact]
    public async Task TwoRequestKindsSurviveReplicaRestartAndRetainIndependentExecutionStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), "bsync-intents-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var complete = Complete.Create("complete-1", "work-1", "v3", new() { Note = "done" });
            var acknowledge = Acknowledge.Create("ack-1", "document-1", "edition-5", new("edition-5"));
            await using (var session = new SyncSession<IntentTestDocument>(new()
            {
                StartReplication = false,
                OpenReplica = async (_, ct) => new(await SqliteLocalStore<IntentTestDocument>.OpenAsync(new() { DataSource = path, Collection = "intents" }, IntentTestJson.Default.IntentTestDocument, ct), "test"),
                CreateTransport = _ => throw new InvalidOperationException("Offline intent creation must not open a transport."),
                Cloner = DocumentCloner.Json(IntentTestJson.Default.IntentTestDocument),
            }))
            {
                // Use the existing replica pending operation store, not an intent-specific outbox.
                var engine = await session.GetEngineAsync("account");
                await engine.WriteAsync(new() { Id = complete.CommandId, Request = complete });
                await engine.WriteAsync(new() { Id = acknowledge.CommandId, Request = acknowledge });
            }
            SqliteStorePool.Release(path);
            var store = await SqliteLocalStore<IntentTestDocument>.OpenAsync(new() { DataSource = path, Collection = "intents" }, IntentTestJson.Default.IntentTestDocument);
            {
                var pending = await store.GetPendingAsync(10);
                Assert.Equal(2, pending.Count);
                var first = await store.GetAsync("complete-1");
                var second = await store.GetAsync("ack-1");
                Assert.Equal("done", Complete.ReadPayload(first!.Current.Request!).Note);
                Assert.Equal("edition-5", Acknowledge.ReadPayload(second!.Current.Request!).Revision);
                var rejected = new IntentStatus(true, new(SyncItemState.Synced), new("complete-1", 1, IntentExecutionState.Rejected, "permission-revoked"));
                Assert.True(rejected.LocallyPersisted);
                Assert.Equal(SyncItemState.Synced, rejected.Transport!.State);
                Assert.Equal(IntentExecutionState.Rejected, rejected.Execution!.State);
                Assert.Null(new IntentStatus(true, new(SyncItemState.Pending), null).Execution);
            }
        }
        finally
        {
            SqliteStorePool.Release(path);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}

public sealed class CompleteWork { public string Note { get; set; } = ""; }
public sealed record AcknowledgeRevision(string Revision);
public sealed class IntentTestDocument : ISyncEntity
{
    public string Id { get; set; } = "";
    public HlcTimestamp UpdatedAt { get; set; }
    public bool Deleted { get; set; }
    public IntentRequest? Request { get; set; }
}
[JsonSerializable(typeof(CompleteWork))]
[JsonSerializable(typeof(AcknowledgeRevision))]
[JsonSerializable(typeof(IntentTestDocument))]
internal partial class IntentTestJson : JsonSerializerContext;
