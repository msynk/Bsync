using System.Security.Claims;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Transport;

namespace Bsync.Testing;

/// <summary>
/// The push/pull semantics every <see cref="ISyncAuthority{TDocument}"/> must provide (docs/protocol/v1.md §4–§6,
/// ADR-002, ADR-005), observed only through the client's <see cref="ISyncTransport{TDocument}"/>. An authority is not
/// supported until every case its driver can run passes. Provider-specific drills (delayed commits, several
/// processes, crashes, restores from real backups) are tested separately per provider.
/// </summary>
public static class AuthorityConformance
{
    private const long Now = 1_000_000;

    /// <summary>All cases.</summary>
    public static IReadOnlyList<AuthorityConformanceCase> Cases { get; } =
    [
        new("Authority: exactly one outcome per operation, in request order", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var outcomes = await Push(client, Op("o1", "a", null), Op("o2", "b", null), Op("o3", "c", null));

            Check.Sequence(["o1", "o2", "o3"], outcomes.Select(o => o.OperationId));
            Check.True(outcomes.All(o => o.Kind == PushOutcomeKind.Accepted));
        }),

        new("Authority I05: accepted versions strictly increase per document; a stale base conflicts", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var v1 = (await Push(client, Op("o1", "a", null, "v1")))[0].Version!.Value;
            var v2 = (await Push(client, Op("o2", "a", v1, "v2")))[0].Version!.Value;
            var stale = (await Push(client, Op("o3", "a", v1, "stale")))[0];

            Check.True(v2 > v1);
            Check.Equal(PushOutcomeKind.Conflict, stale.Kind);
            Check.Equal(v2, stale.Version);
            Check.Equal("v2", stale.Document!.Title);
        }),

        new("Authority I05: two writers on the same base cannot both succeed; a same-id insert conflicts", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var v1 = (await Push(client, Op("o0", "a", null)))[0].Version;
            var first = (await Push(client, Op("oa", "a", v1, "A")))[0];
            var second = (await Push(client, Op("ob", "a", v1, "B")))[0];
            var insert = (await Push(client, Op("oc", "a", null, "C")))[0];

            Check.Equal(PushOutcomeKind.Accepted, first.Kind);
            Check.Equal(PushOutcomeKind.Conflict, second.Kind);
            Check.Equal(PushOutcomeKind.Conflict, insert.Kind);
            Check.Equal("A", Single(await PullAll(client)).Document.Title);
        }),

        new("Authority I04: a duplicate delivery replays the original outcome without a second effect", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var first = (await Push(client, Op("o1", "a", null, "x")))[0];
            var replay = (await Push(client, Op("o1", "a", null, "x")))[0];

            Check.True(!first.IsDuplicate);
            Check.True(replay.IsDuplicate);
            Check.Equal(first.Kind, replay.Kind);
            Check.Equal(first.Version, replay.Version);
            Check.Equal(first.Version, Single(await PullAll(client)).Version);
        }),

        new("Authority I04: a duplicate of a conflicted operation replays the conflict", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            await Push(client, Op("o0", "a", null));
            var first = (await Push(client, Op("o1", "a", null, "late insert")))[0];
            var replay = (await Push(client, Op("o1", "a", null, "late insert")))[0];

            Check.Equal(PushOutcomeKind.Conflict, first.Kind);
            Check.Equal(PushOutcomeKind.Conflict, replay.Kind);
            Check.True(replay.IsDuplicate);
        }),

        new("Authority I04: an operation id reused with a different payload or base is rejected", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var v1 = (await Push(client, Op("o1", "a", null, "x")))[0].Version;
            var differentPayload = (await Push(client, Op("o1", "a", null, "y")))[0];
            var differentBase = (await Push(client, Op("o1", "a", v1, "x")))[0];

            Check.Equal(PushErrorCodes.OperationIdReused, differentPayload.ErrorCode);
            Check.Equal(PushErrorCodes.OperationIdReused, differentBase.ErrorCode);
            Check.Equal("x", Single(await PullAll(client)).Document.Title);
        }),

        new("Authority: malformed operations and two operations for one document are rejected individually", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var outcomes = await Push(
                client,
                Op("o1", "a", null),
                Op("o2", "a", null),
                new PushOperation<ConformanceDocument>("o3", "b", null, Doc("not-b", "t")),
                Op("o4", "c", 0),
                Op("o5", "d", null));

            Check.Sequence(
                [PushOutcomeKind.Accepted, PushOutcomeKind.Rejected, PushOutcomeKind.Rejected, PushOutcomeKind.Rejected, PushOutcomeKind.Accepted],
                outcomes.Select(o => o.Kind));
            Check.True(outcomes.Where(o => o.Kind == PushOutcomeKind.Rejected).All(o => o.ErrorCode == PushErrorCodes.Invalid));
        }),

        new("Authority I12: origin timestamps are stored unchanged; far-future ones are rejected with clock-skew", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);
            var minute = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

            var stamp = new HlcTimestamp(Now - 5, 3, "device");
            var ok = (await Push(client, new PushOperation<ConformanceDocument>("o1", "a", null, new ConformanceDocument { Id = "a", UpdatedAt = stamp })))[0];
            var withinBound = (await Push(client, Op("o2", "b", null, wall: Now + (4 * minute))))[0];
            var future = (await Push(client, Op("o3", "c", null, wall: Now + (60 * minute))))[0];

            Check.Equal(stamp, ok.Document!.UpdatedAt);
            Check.Equal(stamp, (await PullAll(client)).Single(c => c.Document.Id == "a").Document.UpdatedAt);
            Check.Equal(PushOutcomeKind.Accepted, withinBound.Kind);
            Check.Equal(PushOutcomeKind.Rejected, future.Kind);
            Check.Equal(PushErrorCodes.ClockSkew, future.ErrorCode);
            Check.True((await PullAll(client)).All(c => c.Document.Id != "c"));
        }),

        new("Authority I18: application validation rejects without writing", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver, validator: (caller, op, _) => op.Document.Title == "bad" && caller.Scope == SyncCallContext.Anonymous.Scope ? PushErrorCodes.Forbidden : null);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var outcome = (await Push(client, Op("o1", "a", null, "bad")))[0];

            Check.Equal(PushOutcomeKind.Rejected, outcome.Kind);
            Check.Equal(PushErrorCodes.Forbidden, outcome.ErrorCode);
            Check.Equal(0, (await PullAll(client)).Count);
        }),

        new("Authority I06 I10: the feed pages every committed document once, latest version, tombstones included", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var versions = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var i = 0; i < 7; i++)
            {
                versions[$"d{i}"] = (await Push(client, Op($"o{i}", $"d{i}", null)))[0].Version!.Value;
            }

            var tombstone = new ConformanceDocument { Id = "d3", Deleted = true, UpdatedAt = new HlcTimestamp(Now, 1, "n") };
            versions["d3"] = (await Push(client, new PushOperation<ConformanceDocument>("del", "d3", versions["d3"], tombstone)))[0].Version!.Value;

            var feed = await PullAll(client, batch: 3);

            Check.Sequence(
                versions.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"),
                feed.OrderBy(c => c.Document.Id, StringComparer.Ordinal).Select(c => $"{c.Document.Id}={c.Version}"));
            Check.True(feed.Single(c => c.Document.Id == "d3").Document.Deleted);
        }),

        new("Authority I06 I08: pages respect the requested size and advance the checkpoint until the feed is drained", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);
            for (var i = 0; i < 10; i++)
            {
                await Push(client, Op($"o{i}", $"d{i:00}", null));
            }

            var pages = 0;
            var seen = new List<string>();
            var checkpoint = Checkpoint.Start;
            PullResult<ConformanceDocument> page;
            do
            {
                page = await client.PullAsync(new PullRequest(checkpoint, 4));
                Check.True(page.Changes.Count <= 4);
                Check.True(!page.HasMore || page.Checkpoint != checkpoint);
                seen.AddRange(page.Changes.Select(c => c.Document.Id));
                checkpoint = page.Checkpoint;
                pages++;
            }
            while (page.HasMore && pages < 20);

            Check.Equal(10, seen.Count);
            Check.Equal(10, seen.Distinct(StringComparer.Ordinal).Count());
            Check.True(pages >= 3);
            Check.Equal(0, (await client.PullAsync(new PullRequest(checkpoint, 4))).Changes.Count);
        }),

        new("Authority I06: resuming from a checkpoint returns only later changes, and an idle feed is stable", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            await Push(client, Op("o1", "a", null));
            var first = await client.PullAsync(new PullRequest(Checkpoint.Start, 10));
            var idle = await client.PullAsync(new PullRequest(first.Checkpoint, 10));

            await Push(client, Op("o2", "b", null));
            var next = await client.PullAsync(new PullRequest(first.Checkpoint, 10));

            Check.Equal(0, idle.Changes.Count);
            Check.True(!idle.HasMore);
            Check.Sequence(["b"], next.Changes.Select(c => c.Document.Id));
        }),

        new("Authority I14: a write based on a version the authority does not have is accepted when the document does not exist", AuthorityCapabilities.None, async driver =>
        {
            // A replica's pending edit may be based on state a restore lost; it must not be stranded.
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var outcome = (await Push(client, Op("o1", "restored-away", 41, "pending edit")))[0];

            Check.Equal(PushOutcomeKind.Accepted, outcome.Kind);
            Check.True(outcome.Version > 0);
        }),

        new("Authority I14: a checkpoint issued by another authority requires reset", AuthorityCapabilities.None, async driver =>
        {
            await using var one = await CreateAsync(driver);
            await using var other = await CreateAsync(driver);
            var client = one.Connect(SyncCallContext.Anonymous);
            await Push(client, Op("o1", "a", null));
            var checkpoint = (await client.PullAsync(new PullRequest(Checkpoint.Start, 10))).Checkpoint;

            await Check.Throws<SyncResetRequiredException>(() => other.Connect(SyncCallContext.Anonymous).PullAsync(new PullRequest(checkpoint, 10)));
        }),

        new("Authority I07: documents the caller may not read are withheld from pages and outcomes; the checkpoint still advances", AuthorityCapabilities.None, async driver =>
        {
            await using var authority = await CreateAsync(driver, canRead: (_, document) => !document.Title.StartsWith("secret", StringComparison.Ordinal));
            var client = authority.Connect(SyncCallContext.Anonymous);

            await Push(client, Op("o1", "open", null, "public"), Op("o2", "hidden", null, "secret"));
            var page = await client.PullAsync(new PullRequest(Checkpoint.Start, 10));
            var idle = await client.PullAsync(new PullRequest(page.Checkpoint, 10));
            var conflict = (await Push(client, Op("o3", "hidden", null, "guess")))[0];
            var replay = (await Push(client, Op("o3", "hidden", null, "guess")))[0];

            Check.Sequence(["open"], page.Changes.Select(c => c.Document.Id));
            Check.Equal(0, idle.Changes.Count);
            Check.Equal(PushOutcomeKind.Rejected, conflict.Kind);
            Check.Equal(PushErrorCodes.Forbidden, conflict.ErrorCode);
            Check.Null(conflict.Document);
            Check.Equal(PushErrorCodes.Forbidden, replay.ErrorCode);
            Check.Null(replay.Document);
        }),

        new("Authority I07 I14: a changed scope fingerprint refuses the old checkpoint with reason scope-changed", AuthorityCapabilities.None, async driver =>
        {
            var grants = "a";
            await using var authority = await CreateAsync(driver, scopeFingerprint: _ => grants);
            var client = authority.Connect(SyncCallContext.Anonymous);
            await Push(client, Op("o1", "a", null));
            var checkpoint = (await client.PullAsync(new PullRequest(Checkpoint.Start, 10))).Checkpoint;
            var unchanged = await client.PullAsync(new PullRequest(checkpoint, 10));

            grants = "a,b";
            var reset = await Catch<SyncResetRequiredException>(() => client.PullAsync(new PullRequest(checkpoint, 10)));
            var fresh = await client.PullAsync(new PullRequest(Checkpoint.Start, 10));

            Check.Equal(0, unchanged.Changes.Count);
            Check.Equal(ResetReasons.ScopeChanged, reset.Reason);
            Check.Sequence(["a"], fresh.Changes.Select(c => c.Document.Id));
        }),

        new("Authority I07: scopes have isolated feeds, versions and receipts; a checkpoint never crosses scopes", AuthorityCapabilities.ScopeIsolation, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var a = authority.Connect(Caller("tenant-a"));
            var b = authority.Connect(Caller("tenant-b"));

            var inA = (await Push(a, Op("o1", "shared-id", null, "a's")))[0];
            var inB = (await Push(b, Op("o1", "shared-id", null, "b's")))[0];
            await Push(b, Op("o2", "only-b", null));
            var pageA = await a.PullAsync(new PullRequest(Checkpoint.Start, 10));
            var pageB = await b.PullAsync(new PullRequest(Checkpoint.Start, 10));

            Check.Equal(PushOutcomeKind.Accepted, inA.Kind);
            Check.Equal(PushOutcomeKind.Accepted, inB.Kind);
            Check.True(!inB.IsDuplicate);
            Check.Sequence(["shared-id=a's"], pageA.Changes.Select(c => $"{c.Document.Id}={c.Document.Title}"));
            Check.Sequence(["only-b", "shared-id"], pageB.Changes.Select(c => c.Document.Id).Order(StringComparer.Ordinal));
            Check.Equal("b's", pageB.Changes.Single(c => c.Document.Id == "shared-id").Document.Title);
            await Check.Throws<SyncResetRequiredException>(() => b.PullAsync(new PullRequest(pageA.Checkpoint, 10)));
        }),

        new("Authority I04 I05 I19: a dependency group is applied all-or-nothing; aborted members get no receipt; incomplete groups are refused", AuthorityCapabilities.Groups, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var features = (await client.PullAsync(new PullRequest(Checkpoint.Start, 1))).Features;
            var taken = (await Push(client, Op("seed", "taken", null, "exists")))[0].Version;
            var aborted = await Push(
                client,
                Op("g1", "fresh", null, "x") with { Group = "grp", GroupSize = 2 },
                Op("g2", "taken", null, "y") with { Group = "grp", GroupSize = 2 });
            var afterAbort = await PullAll(client);
            var incomplete = await Push(client, Op("g3", "fresh", null, "x") with { Group = "grp2", GroupSize = 2 });
            var retried = await Push(
                client,
                Op("g1", "fresh", null, "x") with { Group = "grp", GroupSize = 2 },
                Op("g4", "taken", taken, "y") with { Group = "grp", GroupSize = 2 });
            var feed = await PullAll(client);

            Check.True(features?.Contains(SyncFeatures.Groups) == true);
            Check.Equal(PushOutcomeKind.RetryLater, aborted[0].Kind);
            Check.Equal(PushErrorCodes.GroupAborted, aborted[0].ErrorCode);
            Check.Equal(PushOutcomeKind.Conflict, aborted[1].Kind);
            Check.Sequence(["taken"], afterAbort.Select(c => c.Document.Id));
            Check.Equal(PushOutcomeKind.Rejected, incomplete[0].Kind);
            Check.Equal(PushErrorCodes.Invalid, incomplete[0].ErrorCode);
            Check.True(retried.All(o => o.Kind == PushOutcomeKind.Accepted && !o.IsDuplicate));
            Check.Sequence(["fresh=x", "taken=y"], feed.Select(c => $"{c.Document.Id}={c.Document.Title}").Order(StringComparer.Ordinal));
        }),

        new("Authority T32 T33 I10: purged tombstones expire older checkpoints and refuse resurrection with base-expired", AuthorityCapabilities.Retention, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var gone = (await Push(client, Op("o1", "gone", null)))[0].Version!.Value;
            await Push(client, Op("o2", "kept", null));
            var early = (await client.PullAsync(new PullRequest(Checkpoint.Start, 1))).Checkpoint;
            var tombstone = new ConformanceDocument { Id = "gone", Deleted = true, UpdatedAt = new HlcTimestamp(Now, 1, "n") };
            var deleted = (await Push(client, new PushOperation<ConformanceDocument>("o3", "gone", gone, tombstone)))[0].Version!.Value;
            var current = (await client.PullAsync(new PullRequest(Checkpoint.Start, 100))).Checkpoint;

            await authority.PurgeTombstonesAsync(SyncCallContext.Anonymous.Scope, deleted);
            var expired = await Catch<SyncResetRequiredException>(() => client.PullAsync(new PullRequest(early, 10)));
            var stillValid = await client.PullAsync(new PullRequest(current, 10));
            var resurrect = (await Push(client, Op("o4", "gone", gone, "edited offline")))[0];
            var recreate = (await Push(client, Op("o5", "gone", null, "written again")))[0];
            var snapshot = await PullAll(client);

            Check.Equal(ResetReasons.Expired, expired.Reason);
            Check.Equal(0, stillValid.Changes.Count);
            Check.Equal(PushOutcomeKind.Rejected, resurrect.Kind);
            Check.Equal(PushErrorCodes.BaseExpired, resurrect.ErrorCode);
            Check.Equal(PushOutcomeKind.Accepted, recreate.Kind);
            Check.Sequence(["gone=written again", "kept=t"], snapshot.Select(c => $"{c.Document.Id}={c.Document.Title}").Order(StringComparer.Ordinal));
        }),

        new("Authority T11 I04: after its receipt is purged, a replayed operation is answered as a conflict and never applied twice", AuthorityCapabilities.Retention, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var v1 = (await Push(client, Op("o1", "a", null, "one")))[0].Version!.Value;
            var v2 = (await Push(client, Op("o2", "a", v1, "two")))[0].Version!.Value;
            await authority.PurgeReceiptsAsync(SyncCallContext.Anonymous.Scope, v2);
            var replay = (await Push(client, Op("o2", "a", v1, "two")))[0];

            Check.Equal(PushOutcomeKind.Conflict, replay.Kind);
            Check.Equal(v2, replay.Version);
            Check.Equal(v2, Single(await PullAll(client)).Version);
        }),

        new("Authority T35 I14: a new epoch refuses older checkpoints with reason epoch, keeps the documents and never reuses a version", AuthorityCapabilities.NewEpoch, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);

            var v1 = (await Push(client, Op("o1", "a", null)))[0].Version!.Value;
            var checkpoint = (await client.PullAsync(new PullRequest(Checkpoint.Start, 10))).Checkpoint;
            var floor = v1 + 1000;

            await authority.BeginNewEpochAsync(floor);
            var reset = await Catch<SyncResetRequiredException>(() => client.PullAsync(new PullRequest(checkpoint, 10)));
            var next = (await Push(client, Op("o2", "b", null)))[0];
            var snapshot = await PullAll(client);

            Check.Equal(ResetReasons.Epoch, reset.Reason);
            Check.True(next.Version > floor);
            Check.Sequence(["a", "b"], snapshot.Select(c => c.Document.Id).Order(StringComparer.Ordinal));
        }),

        new("Authority B3: a published document gets a version and a timestamp; republishing unchanged content adds no feed entry", AuthorityCapabilities.Publisher, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);
            var publisher = authority.Publisher!;
            var scope = SyncCallContext.Anonymous.Scope;

            var first = await publisher.UpsertAsync(scope, new ConformanceDocument { Id = "p", Title = "projected" });
            var page = await client.PullAsync(new PullRequest(Checkpoint.Start, 10));
            var again = await publisher.UpsertAsync(scope, new ConformanceDocument { Id = "p", Title = "projected" });
            var idle = await client.PullAsync(new PullRequest(page.Checkpoint, 10));
            var changed = await publisher.UpsertAsync(scope, new ConformanceDocument { Id = "p", Title = "changed" });
            var next = await client.PullAsync(new PullRequest(page.Checkpoint, 10));

            Check.Equal(new SyncPublishResult(1, 0, 0), first);
            Check.True(Single(page.Changes).Document.UpdatedAt != default);
            Check.Equal(new SyncPublishResult(0, 1, 0), again);
            Check.Equal(0, idle.Changes.Count);
            Check.Equal(new SyncPublishResult(1, 0, 0), changed);
            Check.True(Single(next.Changes).Version > page.Changes[0].Version);
            Check.Equal("changed", next.Changes[0].Document.Title);
        }),

        new("Authority B3 I10: replacing a scope writes changes, keeps unchanged documents and tombstones the ones no longer listed, without a reset", AuthorityCapabilities.Publisher, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);
            var publisher = authority.Publisher!;
            var scope = SyncCallContext.Anonymous.Scope;

            var built = await publisher.ReplaceScopeAsync(scope, [Doc("a", "1"), Doc("b", "1"), Doc("c", "1")]);
            var checkpoint = (await PullAllFrom(client, Checkpoint.Start)).Checkpoint;
            var rebuilt = await publisher.ReplaceScopeAsync(scope, [Doc("a", "1"), Doc("b", "2")]);
            var (changes, _) = await PullAllFrom(client, checkpoint);
            var unchanged = await publisher.ReplaceScopeAsync(scope, [Doc("a", "1"), Doc("b", "2")]);

            Check.Equal(new SyncPublishResult(3, 0, 0), built);
            Check.Equal(new SyncPublishResult(1, 1, 1), rebuilt);
            Check.Sequence(["b=2:False", "c=1:True"], changes.Select(c => $"{c.Document.Id}={c.Document.Title}:{c.Document.Deleted}").Order(StringComparer.Ordinal));
            Check.Equal(new SyncPublishResult(0, 2, 0), unchanged);
        }),

        new("Authority B3 I05: a publisher write is a new version, so a replica's edit based on the older one conflicts", AuthorityCapabilities.Publisher, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);
            var v1 = (await Push(client, Op("o1", "a", null, "replica")))[0].Version;

            await authority.Publisher!.UpsertAsync(SyncCallContext.Anonymous.Scope, Doc("a", "back office"));
            var stale = (await Push(client, Op("o2", "a", v1, "replica edit")))[0];

            Check.Equal(PushOutcomeKind.Conflict, stale.Kind);
            Check.Equal("back office", stale.Document!.Title);
        }),

        new("Authority B3 I10: deleting writes one tombstone; deleting an absent or deleted document changes nothing", AuthorityCapabilities.Publisher, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var client = authority.Connect(SyncCallContext.Anonymous);
            var publisher = authority.Publisher!;
            var scope = SyncCallContext.Anonymous.Scope;
            await publisher.UpsertAsync(scope, Doc("a", "x"));

            var absent = await publisher.DeleteAsync(scope, "missing");
            var deleted = await publisher.DeleteAsync(scope, "a");
            var twice = await publisher.DeleteAsync(scope, "a");
            var feed = await PullAll(client);

            Check.Equal(SyncPublishResult.None, absent);
            Check.Equal(new SyncPublishResult(0, 0, 1), deleted);
            Check.Equal(new SyncPublishResult(0, 1, 0), twice);
            Check.True(Single(feed).Document.Deleted);
            Check.Equal("x", feed[0].Document.Title);
        }),

        new("Authority B3 I07: a fan-out writes one copy per scope, and republishing it adds nothing", AuthorityCapabilities.Publisher | AuthorityCapabilities.ScopeIsolation, async driver =>
        {
            await using var authority = await CreateAsync(driver);
            var publisher = authority.Publisher!;

            var first = await publisher.PublishAsync(Doc("menu", "today"), ["tenant-a", "tenant-b"]);
            var again = await publisher.PublishAsync(Doc("menu", "today"), ["tenant-b", "tenant-a"]);
            var inA = await authority.Connect(Caller("tenant-a")).PullAsync(new PullRequest(Checkpoint.Start, 10));
            var inB = await authority.Connect(Caller("tenant-b")).PullAsync(new PullRequest(Checkpoint.Start, 10));
            var inC = await authority.Connect(Caller("tenant-c")).PullAsync(new PullRequest(Checkpoint.Start, 10));

            Check.Equal(new SyncPublishResult(2, 0, 0), first);
            Check.Equal(new SyncPublishResult(0, 2, 0), again);
            Check.Equal("today", Single(inA.Changes).Document.Title);
            Check.Equal("today", Single(inB.Changes).Document.Title);
            Check.Equal(0, inC.Changes.Count);
        }),

        new("Authority C1 I07 I08: with read membership a caller's pages are full and hold only what it may read", AuthorityCapabilities.Membership, async driver =>
        {
            await using var authority = await CreateAsync(driver, membership: true);
            var writer = authority.Connect(User("writer"));
            for (var i = 0; i < 30; i++)
            {
                await Push(writer, Op($"o{i}", $"d{i:00}", null, i % 3 == 0 ? "x|writer,alice" : "x|writer,bob"));
            }

            var alice = authority.Connect(User("alice"));
            var seen = new List<string>();
            var checkpoint = Checkpoint.Start;
            PullResult<ConformanceDocument> page;
            do
            {
                page = await alice.PullAsync(WithRemovals(checkpoint, 4));
                Check.True(page.HasMore ? page.Changes.Count == 4 : page.Changes.Count <= 4);
                seen.AddRange(page.Changes.Select(c => c.Document.Id));
                checkpoint = page.Checkpoint;
            }
            while (page.HasMore);

            Check.Sequence(Enumerable.Range(0, 30).Where(i => i % 3 == 0).Select(i => $"d{i:00}"), seen.Order(StringComparer.Ordinal));
        }),

        new("Authority C1 I10 I14: revoking read access is a removal for replicas that ask for it, and scope-changed for others", AuthorityCapabilities.Membership, async driver =>
        {
            await using var authority = await CreateAsync(driver, membership: true);
            var alice = authority.Connect(User("alice"));
            var bob = authority.Connect(User("bob"));
            var v1 = (await Push(alice, Op("o1", "shared", null, "x|alice,bob")))[0].Version;
            var bobs = (await bob.PullAsync(WithRemovals(Checkpoint.Start, 10))).Checkpoint;

            await Push(alice, Op("o2", "shared", v1, "y|alice"));
            var removal = await bob.PullAsync(WithRemovals(bobs, 10));
            var legacy = await Catch<SyncResetRequiredException>(() => bob.PullAsync(new PullRequest(bobs, 10)));
            var alices = await alice.PullAsync(WithRemovals(Checkpoint.Start, 10));
            var fresh = await bob.PullAsync(WithRemovals(Checkpoint.Start, 10));

            Check.Equal(0, removal.Changes.Count);
            Check.Sequence(["shared"], removal.Removals ?? []);
            Check.True(removal.Features?.Contains(SyncFeatures.Removals) == true);
            Check.Equal(ResetReasons.ScopeChanged, legacy.Reason);
            Check.Equal("y|alice", Single(alices.Changes).Document.Title);
            Check.Equal(0, fresh.Changes.Count + (fresh.Removals?.Count ?? 0)); // a snapshot never mentions what bob cannot see
        }),

        new("Authority C1 I07: a non-reader cannot write or see a document; regranting brings it back", AuthorityCapabilities.Membership, async driver =>
        {
            await using var authority = await CreateAsync(driver, membership: true);
            var alice = authority.Connect(User("alice"));
            var bob = authority.Connect(User("bob"));
            var v1 = (await Push(alice, Op("o1", "doc", null, "x|alice")))[0].Version;
            var bobs = (await bob.PullAsync(WithRemovals(Checkpoint.Start, 10))).Checkpoint;

            var refused = (await Push(bob, Op("b1", "doc", v1, "hijack|bob")))[0];
            var v2 = (await Push(alice, Op("o2", "doc", v1, "x|alice,bob")))[0].Version;
            var granted = await bob.PullAsync(WithRemovals(bobs, 10));
            var edit = (await Push(bob, Op("b2", "doc", v2, "edited by bob|alice,bob")))[0];

            Check.Equal(PushOutcomeKind.Rejected, refused.Kind);
            Check.Equal(PushErrorCodes.Forbidden, refused.ErrorCode);
            Check.Null(refused.Document);
            Check.Equal("x|alice,bob", Single(granted.Changes).Document.Title);
            Check.Equal(PushOutcomeKind.Accepted, edit.Kind);
        }),

        new("Authority C1 I07: a checkpoint of one principal is not resumed for another", AuthorityCapabilities.Membership, async driver =>
        {
            await using var authority = await CreateAsync(driver, membership: true);
            var alice = authority.Connect(User("alice"));
            await Push(alice, Op("o1", "doc", null, "x|alice,bob"));
            var alices = (await alice.PullAsync(WithRemovals(Checkpoint.Start, 10))).Checkpoint;

            await Check.Throws<SyncResetRequiredException>(() => authority.Connect(User("bob")).PullAsync(WithRemovals(alices, 10)));
        }),
    ];

    /// <summary>The cases a driver with <paramref name="capabilities"/> can run.</summary>
    public static IEnumerable<AuthorityConformanceCase> CasesFor(AuthorityCapabilities capabilities) =>
        Cases.Where(c => (c.Requires & ~capabilities) == 0);

    private static Task<AuthorityUnderTest> CreateAsync(
        IAuthorityConformanceDriver driver,
        Func<SyncCallContext, PushOperation<ConformanceDocument>, ConformanceDocument?, string?>? validator = null,
        Func<SyncCallContext, ConformanceDocument, bool>? canRead = null,
        Func<SyncCallContext, string>? scopeFingerprint = null,
        bool membership = false) =>
        driver.CreateAsync(new AuthorityConformanceOptions
        {
            Clock = new FixedClock(Now),
            Validator = validator,
            CanRead = canRead,
            ScopeFingerprint = scopeFingerprint,
            Readers = membership ? ReadersOf : null,
            PrincipalKey = membership ? static caller => caller.Principal.FindFirst(ClaimTypes.Name)?.Value : null,
        });

    // Membership cases encode the readers in the title: "text|alice,bob".
    private static IEnumerable<string> ReadersOf(ConformanceDocument document) =>
        document.Title.Split('|') is [_, var readers] ? readers.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];

    private static SyncCallContext User(string name, string scope = "default") =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "conformance")), scope);

    private static PullRequest WithRemovals(Checkpoint since, int limit) => new(since, limit) { Features = [SyncFeatures.Removals] };

    private static SyncCallContext Caller(string scope) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "conformance")], "conformance")), scope);

    private static ConformanceDocument Doc(string id, string title, long wall = Now) =>
        new() { Id = id, Title = title, UpdatedAt = new HlcTimestamp(wall, 0, "n") };

    private static PushOperation<ConformanceDocument> Op(string opId, string docId, long? baseVersion, string title = "t", long wall = Now) =>
        new(opId, docId, baseVersion, Doc(docId, title, wall));

    private static async Task<IReadOnlyList<PushOutcome<ConformanceDocument>>> Push(ISyncTransport<ConformanceDocument> client, params PushOperation<ConformanceDocument>[] operations)
    {
        var outcomes = (await client.PushAsync(new PushRequest<ConformanceDocument>(operations)).ConfigureAwait(false)).Outcomes;
        Check.Equal(operations.Length, outcomes.Count);
        return outcomes;
    }

    private static async Task<List<RemoteChange<ConformanceDocument>>> PullAll(ISyncTransport<ConformanceDocument> client, int batch = 2)
    {
        var all = new List<RemoteChange<ConformanceDocument>>();
        var checkpoint = Checkpoint.Start;
        for (var pages = 0; pages < 1000; pages++)
        {
            var page = await client.PullAsync(new PullRequest(checkpoint, batch)).ConfigureAwait(false);
            Check.True(page.Changes.Count <= batch);
            Check.Equal(page.Changes.Count, page.Changes.Select(c => c.Document.Id).Distinct(StringComparer.Ordinal).Count());
            Check.True(!page.HasMore || page.Checkpoint != checkpoint);
            all.AddRange(page.Changes);
            checkpoint = page.Checkpoint;
            if (!page.HasMore)
            {
                return all;
            }
        }

        throw new ConformanceFailure("The feed did not end after 1000 pages.");
    }

    private static async Task<(List<RemoteChange<ConformanceDocument>> Changes, Checkpoint Checkpoint)> PullAllFrom(ISyncTransport<ConformanceDocument> client, Checkpoint checkpoint)
    {
        var all = new List<RemoteChange<ConformanceDocument>>();
        for (var pages = 0; pages < 1000; pages++)
        {
            var page = await client.PullAsync(new PullRequest(checkpoint, 50)).ConfigureAwait(false);
            all.AddRange(page.Changes);
            checkpoint = page.Checkpoint;
            if (!page.HasMore)
            {
                return (all, checkpoint);
            }
        }

        throw new ConformanceFailure("The feed did not end after 1000 pages.");
    }

    private static T Single<T>(IReadOnlyList<T> items)
    {
        Check.Equal(1, items.Count);
        return items[0];
    }

    private static async Task<TException> Catch<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new ConformanceFailure($"Expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}");
        }

        throw new ConformanceFailure($"Expected {typeof(TException).Name}, nothing was thrown.");
    }
}
