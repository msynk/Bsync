using System.Security.Claims;
using Bsync.Conflicts;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>
/// Phase 8: access changes (revoke, regrant, filter change) and retention (purged tombstones, expired checkpoints,
/// receipt expiry), all through the reset machinery (docs/protocol.md §6.1).
/// </summary>
public sealed class SelectiveSyncTests
{
    /// <summary>An authority whose read/write rules come from a mutable set of grants, fingerprinted per caller.</summary>
    private sealed class Grants
    {
        public HashSet<string> Visible { get; } = new(StringComparer.Ordinal);

        public int Version { get; private set; }

        public void Grant(params string[] ids)
        {
            Visible.UnionWith(ids);
            Version++;
        }

        public void Revoke(params string[] ids)
        {
            Visible.ExceptWith(ids);
            Version++;
        }

        public InMemorySyncServer<Note> Server()
        {
            var options = NoteJson.ServerOptions();
            return new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
            {
                Cloner = options.Cloner,
                Fingerprint = options.Fingerprint,
                PhysicalClock = options.PhysicalClock,
                CanRead = (context, note) => IsAdmin(context) || Visible.Contains(note.Id),
                CanWrite = (context, op, _) => IsAdmin(context) || Visible.Contains(op.DocumentId),
                ScopeFingerprint = context => IsAdmin(context) ? "admin" : $"grants:{Version}",
            });
        }

        private static bool IsAdmin(SyncCallContext context) => context.Principal.Identity?.Name == "admin";
    }

    private static SyncCallContext As(string user) => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test")), "default");

    private static TestReplica Replica(InMemorySyncServer<Note> server, string user, IConflictHandler<Note>? handler = null) =>
        new(new InMemorySyncServerRef(server), user, handler, transport: _ => new Bsync.Server.InProcessTransport<Note>(server, As(user)));

    [Fact(DisplayName = "T37 I07 I10: revoked documents are removed from the device; regranted ones return unchanged")]
    public async Task RevokeAndRegrant()
    {
        var grants = new Grants();
        var server = grants.Server();
        var admin = Replica(server, "admin");
        await admin.Engine.WriteAsync(new Note { Id = "a", Title = "shared" });
        await admin.Engine.WriteAsync(new Note { Id = "b", Title = "secret later" });
        await admin.Engine.SyncAsync();
        grants.Grant("a", "b");

        var alice = Replica(server, "alice");
        await alice.Engine.SyncAsync();
        Assert.Equal(["a", "b"], (await alice.Engine.QueryAsync()).Select(n => n.Id).Order());

        grants.Revoke("b");
        var afterRevoke = await alice.Engine.SyncAsync();

        Assert.True(afterRevoke.ResetPerformed);
        Assert.Equal(1, afterRevoke.PurgedAfterReset);
        Assert.Null(await alice.Engine.GetAsync("b")); // removed from the device, not merely hidden
        Assert.NotNull(server.GetVersion("b")); // not deleted on the server

        grants.Grant("b");
        var afterRegrant = await alice.Engine.SyncAsync();

        Assert.True(afterRegrant.ResetPerformed);
        Assert.Equal("secret later", (await alice.RecordAsync("b")).Current.Title);
        Assert.Equal(["a", "b"], (await alice.Engine.QueryAsync()).Select(n => n.Id).Order());
    }

    [Fact(DisplayName = "T36 I07 I19: a pending edit to a document that is no longer authorized is rejected and kept, never dropped")]
    public async Task PendingEditOnRevokedDocument()
    {
        var grants = new Grants();
        var server = grants.Server();
        var admin = Replica(server, "admin");
        await admin.Engine.WriteAsync(new Note { Id = "b", Title = "original" });
        await admin.Engine.SyncAsync();
        grants.Grant("b");
        var alice = Replica(server, "alice");
        await alice.Engine.SyncAsync();

        await alice.Engine.WriteAsync(new Note { Id = "b", Title = "edited offline" });
        grants.Revoke("b");
        var result = await alice.Engine.SyncAsync();

        Assert.Equal(1, result.Rejected);
        var record = await alice.RecordAsync("b");
        Assert.Equal("edited offline", record.Current.Title);
        Assert.Equal(PushErrorCodes.Forbidden, record.Rejection!.ErrorCode);
        Assert.Equal("original", server.Snapshot().Single().Title);
    }

    [Fact(DisplayName = "T37 I01 I11: a revoked document with a kept conflict is hidden, not purged, and the conflict stays listed")]
    public async Task RevokedConflictIsKept()
    {
        var grants = new Grants();
        var server = grants.Server();
        var admin = Replica(server, "admin");
        await admin.Engine.WriteAsync(new Note { Id = "b", Title = "base" });
        await admin.Engine.SyncAsync();
        grants.Grant("b");
        var alice = Replica(server, "alice");
        await alice.Engine.SyncAsync();
        await admin.Engine.WriteAsync(new Note { Id = "b", Title = "theirs" });
        await admin.Engine.SyncAsync();
        await alice.Engine.WriteAsync(new Note { Id = "b", Title = "mine" });
        Assert.Equal(1, (await alice.Engine.SyncAsync()).Conflicts);

        grants.Revoke("b");
        var result = await alice.Engine.SyncAsync();

        Assert.True(result.ResetPerformed);
        Assert.Equal((0, 1), (result.PurgedAfterReset, result.MissingAfterReset));
        var record = await alice.RecordAsync("b");
        Assert.True(record.MissingAfterReset);
        Assert.Equal("mine", record.Conflict!.Local.Title);
        Assert.Empty(await alice.Engine.QueryAsync());
        Assert.Single(await alice.Engine.GetConflictsAsync());

        // Resolving is still possible; the server decides (here: forbidden, so it is kept as a rejection).
        await alice.Engine.ResolveConflictAsync("b", new Note { Id = "b", Title = "mine, resolved" });
        Assert.Equal(1, (await alice.Engine.SyncAsync()).Rejected);
        Assert.Equal("theirs", server.Snapshot().Single().Title);
    }

    [Fact(DisplayName = "T38 I07: a changed filter cannot reuse the old checkpoint")]
    public async Task FilterChange()
    {
        var category = "work";
        var options = NoteJson.ServerOptions();
        var server = new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
        {
            Cloner = options.Cloner,
            Fingerprint = options.Fingerprint,
            PhysicalClock = options.PhysicalClock,
            CanRead = (_, note) => note.Body == category,
            ScopeFingerprint = _ => $"category={category}",
        });
        server.Push(new PushRequest<Note>(
        [
            new("o1", "w1", null, new Note { Id = "w1", Body = "work" }),
            new("o2", "h1", null, new Note { Id = "h1", Body = "home" }),
        ]));
        var client = Replica(server, "alice");
        await client.Engine.SyncAsync();
        Assert.Equal(["w1"], (await client.Engine.QueryAsync()).Select(n => n.Id));

        category = "home";
        var result = await client.Engine.SyncAsync();

        Assert.True(result.ResetPerformed);
        Assert.Equal(["h1"], (await client.Engine.QueryAsync()).Select(n => n.Id)); // not ["h1", "w1"] and not []
    }

    [Fact(DisplayName = "T33 I10 I14: after tombstones are purged, a long-offline replica resets and does not resurrect the deleted document")]
    public async Task PurgedTombstonesDoNotResurrect()
    {
        var server = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        var active = Replica(server, "active");
        var offline = Replica(server, "offline");
        await active.Engine.WriteAsync(new Note { Id = "doomed" });
        await active.Engine.WriteAsync(new Note { Id = "kept" });
        await active.Engine.SyncAsync();
        await offline.Engine.SyncAsync();

        await active.Engine.DeleteAsync("doomed");
        await active.Engine.SyncAsync();
        var deletedAt = server.GetVersion("doomed")!.Value;
        await active.Engine.WriteAsync(new Note { Id = "kept", Title = "after purge" });
        await active.Engine.SyncAsync(); // its pull reaches the tombstone before it pushes the new write

        // The horizon trails the newest data (a retention period); purge through the tombstone.
        Assert.Equal(1, server.PurgeTombstones(deletedAt));

        // A replica that has pulled up to the horizon keeps syncing incrementally.
        Assert.False((await active.Engine.SyncAsync()).ResetPerformed);

        var result = await offline.Engine.SyncAsync();

        Assert.True(result.ResetPerformed);
        Assert.Equal(1, result.PurgedAfterReset);
        Assert.Null(await offline.Engine.GetAsync("doomed"));
        Assert.Equal("after purge", (await offline.RecordAsync("kept")).Current.Title);
        Assert.Null(server.GetVersion("doomed"));
    }

    [Fact(DisplayName = "T32 I10: an edit based on a purged document is rejected; writing it again recreates it explicitly")]
    public async Task ExplicitRestoreAfterPurge()
    {
        var server = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        var active = Replica(server, "active");
        var offline = Replica(server, "offline");
        await active.Engine.WriteAsync(new Note { Id = "doc", Title = "v1" });
        await active.Engine.SyncAsync();
        await offline.Engine.SyncAsync();
        await offline.Engine.WriteAsync(new Note { Id = "doc", Title = "edited long ago" });

        await active.Engine.DeleteAsync("doc");
        await active.Engine.SyncAsync();
        server.PurgeTombstones(server.HighestVersion);

        var rejected = await offline.Engine.SyncAsync();
        Assert.Equal(1, rejected.Rejected);
        var parked = await offline.RecordAsync("doc");
        Assert.Equal(PushErrorCodes.BaseExpired, parked.Rejection!.ErrorCode);
        Assert.Null(server.GetVersion("doc"));

        await offline.Engine.WriteAsync(new Note { Id = "doc", Title = "restored on purpose" });
        var restored = await offline.Engine.SyncAsync();

        Assert.Equal(1, restored.Pushed);
        Assert.Equal("restored on purpose", server.Snapshot().Single().Title);
    }

    [Fact(DisplayName = "T11 I04 I11: after its receipt expires, a replayed operation is answered as a conflict, never applied twice")]
    public async Task ReceiptExpiry()
    {
        var server = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        var client = Replica(server, "a", new ServerWinsConflictHandler<Note>());
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "once" });
        client.Transport.LoseResponses = 1;
        await Assert.ThrowsAsync<InjectedFaultException>(() => client.Engine.PushAsync());
        var version = server.GetVersion("n1");
        Assert.Equal(1, server.PurgeReceipts(server.HighestVersion));

        var retry = await client.Engine.PushAsync();

        Assert.Equal(1, retry.Conflicts);
        Assert.Equal(version, server.GetVersion("n1")); // not written a second time
        Assert.False((await client.RecordAsync("n1")).IsDirty);
    }
}
