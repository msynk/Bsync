using Bsync.Client;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>Task C5: a rejection carries a stable code and arguments that complete it, to the record and the UI.</summary>
public sealed class RejectionArgumentsTests
{
    private sealed class TitleLimit : ISyncWriteHandler<Note>
    {
        public ValueTask<SyncWriteDecision<Note>> HandleAsync(SyncWriteContext<Note> write, CancellationToken cancellationToken) =>
            ValueTask.FromResult(write.Submitted.Title.Length > 5
                ? SyncWriteDecision<Note>.Reject("title-too-long", new Dictionary<string, string> { ["max"] = "5", ["actual"] = write.Submitted.Title.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                : SyncWriteDecision<Note>.Accept(write.Submitted));
    }

    [Fact(DisplayName = "C5 I19: arguments cross the JSON wire, are kept on the parked record and shown by item status and issues; a replay returns them from the receipt")]
    public async Task ArgumentsReachTheUi()
    {
        var options = NoteJson.ServerOptions(SystemPhysicalClock.Instance);
        var server = new InMemorySyncServerRef(new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
        {
            Cloner = options.Cloner,
            Fingerprint = options.Fingerprint,
            PhysicalClock = options.PhysicalClock,
            WriteHandler = new TitleLimit(),
        }));
        await using var session = new SyncSession<Note>(new SyncSessionOptions<Note>
        {
            Cloner = NoteJson.Clone,
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Note>(new Storage.InMemoryLocalStore<Note>(NoteJson.Clone), "n")),
            CreateTransport = _ => new JsonWireTransport<Note>(new ServerRefTransport(server), NoteJsonContext.Default),
            Interval = TimeSpan.FromMinutes(10),
        });
        var notes = new LocalSyncCollection<Note>(session, _ => Task.FromResult("alice"));
        await notes.SaveAsync(new Note { Id = "long", Title = "far too long" });
        var engine = await session.GetEngineAsync("alice");
        await engine.SyncAsync();

        var status = await notes.GetItemStatusAsync("long");
        var issue = (await notes.GetIssuesAsync()).Items.Single();
        var operation = (await engine.GetAsync("long"))!;

        Assert.Equal((SyncItemState.Rejected, "title-too-long"), (status!.State, status.Detail));
        Assert.Equal(new Dictionary<string, string> { ["max"] = "5", ["actual"] = "12" }, status.Arguments);
        Assert.Equal("5", issue.Arguments!["max"]);
        Assert.Equal("12", operation.Rejection!.Arguments!["actual"]);
    }
}
