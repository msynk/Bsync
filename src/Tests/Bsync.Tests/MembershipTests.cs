using System.Security.Claims;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>Task C1 / ADR-015: read membership in the feed and removals without a resnapshot, end to end through the engine.</summary>
public sealed class MembershipTests
{
    // Readers are listed in the note's body: "alice,bob".
    private static InMemorySyncServerRef Server(Func<Note, IEnumerable<string>>? readers = null)
    {
        var options = NoteJson.ServerOptions(SystemPhysicalClock.Instance);
        return new InMemorySyncServerRef(new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
        {
            Cloner = options.Cloner,
            Fingerprint = options.Fingerprint,
            PhysicalClock = options.PhysicalClock,
            Readers = readers ?? (note => note.Body.Split(',', StringSplitOptions.RemoveEmptyEntries)),
            PrincipalKey = caller => caller.Principal.FindFirst(ClaimTypes.Name)?.Value,
        }));
    }

    private static SyncCallContext User(string name) => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test")), "team");

    private static TestReplica Replica(InMemorySyncServerRef server, string user) =>
        new(server, user, physicalClock: SystemPhysicalClock.Instance, transport: _ => new InProcessTransport<Note>(server.Server, User(user)));

    [Fact(DisplayName = "C1 I05 I11: two readers editing one shared document get a real conflict")]
    public async Task SharedDocumentConflicts()
    {
        var server = Server();
        var alice = Replica(server, "alice");
        var bob = Replica(server, "bob");
        await alice.Engine.WriteAsync(new Note { Id = "plan", Title = "v1", Body = "alice,bob" });
        await alice.Engine.SyncAsync();
        await bob.Engine.SyncAsync();

        await alice.Engine.WriteAsync(new Note { Id = "plan", Title = "alice's", Body = "alice,bob" });
        await bob.Engine.WriteAsync(new Note { Id = "plan", Title = "bob's", Body = "alice,bob" });
        await alice.Engine.SyncAsync();
        var second = await bob.Engine.SyncAsync();

        Assert.Equal(1, second.Conflicts);
        Assert.NotNull((await bob.RecordAsync("plan")).Conflict); // kept for bob to decide (default policy)
        Assert.Equal("alice's", server.Get("plan").Title);
    }

    [Fact(DisplayName = "C1 I10 I14: revoking access removes a clean copy without a reset; a dirty draft is hidden, parked, and back after a regrant")]
    public async Task RevocationRemovesWithoutReset()
    {
        var server = Server();
        var alice = Replica(server, "alice");
        var bob = Replica(server, "bob");
        await alice.Engine.WriteAsync(new Note { Id = "clean", Title = "x", Body = "alice,bob" });
        await alice.Engine.WriteAsync(new Note { Id = "draft", Title = "x", Body = "alice,bob" });
        await alice.Engine.WriteAsync(new Note { Id = "other", Title = "x", Body = "alice,bob" });
        await alice.Engine.SyncAsync();
        await bob.Engine.SyncAsync();
        await bob.Engine.WriteAsync(new Note { Id = "draft", Title = "bob's unsent edit", Body = "alice,bob" });

        // Alice takes bob off two documents.
        await alice.Engine.WriteAsync(new Note { Id = "clean", Title = "x", Body = "alice" });
        await alice.Engine.WriteAsync(new Note { Id = "draft", Title = "x", Body = "alice" });
        await alice.Engine.SyncAsync();
        var result = await bob.Engine.SyncAsync();

        Assert.False(result.ResetPerformed);
        Assert.Equal(2, result.Removed);
        Assert.Null(await bob.Engine.GetAsync("clean")); // gone from the device, still on the server
        Assert.NotNull(server.Server.GetVersion("clean"));
        var draft = await bob.RecordAsync("draft");
        Assert.True(draft.MissingAfterReset); // hidden from queries, kept
        Assert.Equal(PushErrorCodes.Forbidden, draft.Rejection!.ErrorCode); // its upload was refused and parked
        Assert.Equal(["other"], (await bob.Engine.QueryAsync()).Select(n => n.Id));
        Assert.Equal("x", server.Get("draft").Title); // the draft never reached the server

        // Regranted: the clean copy returns; the draft is visible again with its edit.
        await alice.Engine.WriteAsync(new Note { Id = "clean", Title = "x", Body = "alice,bob" });
        await alice.Engine.WriteAsync(new Note { Id = "draft", Title = "x", Body = "alice,bob" });
        await alice.Engine.SyncAsync();
        await bob.Engine.SyncAsync();

        Assert.NotNull(await bob.Engine.GetAsync("clean"));
        Assert.Equal("bob's unsent edit", (await bob.Engine.QueryAsync()).Single(n => n.Id == "draft").Title);
    }

    [Fact(DisplayName = "C1 I14: a moving time window based on membership never emits scope-changed")]
    public async Task MovingWindowDoesNotReset()
    {
        var today = 10;
        var server = Server(note => int.Parse(note.Title, System.Globalization.CultureInfo.InvariantCulture) > today - 3 ? ["bob"] : []);
        var bob = Replica(server, "bob");
        for (var day = 1; day <= 10; day++)
        {
            await server.Server.UpsertAsync("team", new Note { Id = $"day{day:00}", Title = $"{day}" });
        }

        await bob.Engine.SyncAsync();
        var before = (await bob.Engine.QueryAsync()).Select(n => n.Id).ToList();
        var resets = 0;
        for (var day = 11; day <= 14; day++)
        {
            today = day;
            await server.Server.UpsertAsync("team", new Note { Id = $"day{day:00}", Title = $"{day}" });
            await server.Server.ReplaceScopeAsync("team", Enumerable.Range(1, day).Select(d => new Note { Id = $"day{d:00}", Title = $"{d}" }));
            resets += (await bob.Engine.SyncAsync()).ResetPerformed ? 1 : 0;
        }

        Assert.Equal(["day08", "day09", "day10"], before);
        Assert.Equal(0, resets);
        Assert.Equal(["day12", "day13", "day14"], (await bob.Engine.QueryAsync()).Select(n => n.Id).Order());
    }
}
