using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>The HTTP binding (docs/protocol/v1.md §8): headers, errors, limits, authentication and scopes.</summary>
public sealed class HttpBindingTests
{
    private static InMemorySyncServer<Note> Server(
        Func<SyncCallContext, Note, bool>? canRead = null,
        Func<SyncCallContext, PushOperation<Note>, Note?, bool>? canWrite = null,
        int maxOperationsPerPush = 1000)
    {
        var options = NoteJson.ServerOptions(maxOperationsPerPush: maxOperationsPerPush);
        return new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
        {
            Cloner = options.Cloner,
            Fingerprint = options.Fingerprint,
            PhysicalClock = options.PhysicalClock,
            MaxOperationsPerPush = maxOperationsPerPush,
            CanRead = canRead,
            CanWrite = canWrite,
        });
    }

    private static PushRequest<Note> Push(params (string Op, string Id, long? Base)[] ops) =>
        new(ops.Select(o => new PushOperation<Note>(o.Op, o.Id, o.Base, new Note { Id = o.Id, Title = o.Id, UpdatedAt = new HlcTimestamp(1_000, 0, "n") })).ToList());

    private static HttpRequestMessage Raw(string path, string body, string? protocol = "1", string? schema = SyncTestHost.SchemaId, string contentType = "application/json")
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (protocol is not null)
        {
            message.Headers.Add("Bsync-Protocol", protocol);
        }

        if (schema is not null)
        {
            message.Headers.Add("Bsync-Schema", schema);
        }

        return message;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> SendRawAsync(HttpClient client, HttpRequestMessage message)
    {
        using var response = await client.SendAsync(message);
        var problem = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
            ? await response.Content.ReadFromJsonAsync<Dictionary<string, object>>()
            : null;
        return (response.StatusCode, problem?.GetValueOrDefault("code")?.ToString());
    }

    [Theory(DisplayName = "I17: missing or unsupported protocol and schema headers are refused before reading the body")]
    [InlineData(null, SyncTestHost.SchemaId, HttpStatusCode.BadRequest, "invalid-request")]
    [InlineData("2", SyncTestHost.SchemaId, HttpStatusCode.BadRequest, "upgrade-required")]
    [InlineData("1", "notes-v0", HttpStatusCode.BadRequest, "upgrade-required")]
    [InlineData("1", null, HttpStatusCode.BadRequest, "upgrade-required")]
    public async Task HeadersEnforced(string? protocol, string? schema, HttpStatusCode status, string code)
    {
        await using var host = await SyncTestHost.StartAsync(Server());
        var result = await SendRawAsync(host.Client(), Raw("sync/collections/notes/pull", """{"checkpoint":null,"limit":1}""", protocol, schema));
        Assert.Equal((status, code), result);
    }

    [Fact(DisplayName = "I17: the typed client surfaces upgrade-required as a permanent error")]
    public async Task UpgradeRequiredIsPermanent()
    {
        await using var host = await SyncTestHost.StartAsync(Server());
        var error = await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport(schemaId: "notes-v0").PullAsync(new PullRequest(Checkpoint.Start, 1)));
        Assert.Equal(SyncErrorCodes.UpgradeRequired, error.ErrorCode);
        Assert.False(error.IsTransient);
        Assert.Equal(400, error.StatusCode);
    }

    [Theory(DisplayName = "I09: malformed bodies and non-JSON content are refused")]
    [InlineData("""{"checkpoint":null}""", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("""{"checkpoint":null,"limit":0}""", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("not json", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("""{"checkpoint":null,"limit":1}""", "text/plain", HttpStatusCode.UnsupportedMediaType)]
    public async Task MalformedRequests(string body, string contentType, HttpStatusCode status)
    {
        await using var host = await SyncTestHost.StartAsync(Server());
        var result = await SendRawAsync(host.Client(), Raw("sync/collections/notes/pull", body, contentType: contentType));
        Assert.Equal((status, "invalid-request"), result);
    }

    [Fact(DisplayName = "I08: too many operations or an oversized body is refused with payload-too-large")]
    public async Task Limits()
    {
        await using var host = await SyncTestHost.StartAsync(Server(maxOperationsPerPush: 2), maxRequestBodyBytes: 2_000);

        var tooMany = await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport().PushAsync(Push(("a", "a", null), ("b", "b", null), ("c", "c", null))));
        var tooBig = await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport().PushAsync(new PushRequest<Note>(
            [new PushOperation<Note>("big", "big", null, new Note { Id = "big", Body = new string('x', 5_000) })])));

        Assert.Equal((SyncErrorCodes.PayloadTooLarge, 413), (tooMany.ErrorCode, tooMany.StatusCode));
        Assert.Equal((SyncErrorCodes.PayloadTooLarge, 413), (tooBig.ErrorCode, tooBig.StatusCode));
        Assert.False(tooMany.IsTransient);
    }

    [Fact(DisplayName = "I07: an unauthenticated caller is refused when authentication is required")]
    public async Task AuthenticationRequired()
    {
        await using var host = await SyncTestHost.StartAsync(new ScopedAuthority<Note>(_ => Server(), new AuthorityLimits(1000, 1000)), requireAuthentication: true);

        var error = await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport().PullAsync(new PullRequest(Checkpoint.Start, 10)));
        var noTenant = await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport(user: "alice").PullAsync(new PullRequest(Checkpoint.Start, 10)));

        Assert.Equal((SyncErrorCodes.Unauthorized, 401), (error.ErrorCode, error.StatusCode));
        Assert.Equal((SyncErrorCodes.Forbidden, 403), (noTenant.ErrorCode, noTenant.StatusCode));
    }

    [Fact(DisplayName = "T36 T40 I07: tenants never see each other's documents, versions, receipts or checkpoints")]
    public async Task TenantIsolation()
    {
        await using var host = await SyncTestHost.StartAsync(new ScopedAuthority<Note>(_ => Server(), new AuthorityLimits(1000, 1000)), requireAuthentication: true);
        var alice = host.Transport("alice", "tenant-a");
        var bob = host.Transport("bob", "tenant-b");

        var a = (await alice.PushAsync(Push(("op-1", "shared-id", null)))).Outcomes[0];
        var b = (await bob.PushAsync(Push(("op-1", "shared-id", null)))).Outcomes[0]; // same op id and doc id

        Assert.Equal(PushOutcomeKind.Accepted, a.Kind);
        Assert.Equal(PushOutcomeKind.Accepted, b.Kind);
        Assert.False(b.IsDuplicate); // not a replay of tenant A's receipt

        var alicePage = await alice.PullAsync(new PullRequest(Checkpoint.Start, 10));
        var bobPage = await bob.PullAsync(new PullRequest(Checkpoint.Start, 10));
        Assert.Single(alicePage.Changes);
        Assert.Single(bobPage.Changes);

        // A checkpoint from one tenant is not valid for another.
        await Assert.ThrowsAsync<SyncResetRequiredException>(() => bob.PullAsync(new PullRequest(alicePage.Checkpoint, 10)));

        // Tenant id in a header without authentication grants nothing (the scope comes from claims only).
        await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport(tenant: "tenant-a").PullAsync(new PullRequest(Checkpoint.Start, 10)));
    }

    [Fact(DisplayName = "T40 I07: conflict and replayed outcomes never reveal a document the caller may not read")]
    public async Task ConflictDoesNotLeak()
    {
        static bool Owns(SyncCallContext context, Note note) => note.Title.StartsWith(context.Principal.Identity?.Name ?? "?", StringComparison.Ordinal);
        await using var host = await SyncTestHost.StartAsync(Server(canRead: Owns), requireAuthentication: true);
        var alice = host.Transport("alice", "t");
        var mallory = host.Transport("mallory", "t");
        await alice.PushAsync(new PushRequest<Note>([new("a1", "secret", null, new Note { Id = "secret", Title = "alice's secret", UpdatedAt = new HlcTimestamp(1_000, 0, "n") })]));

        using var raw = await host.Client("mallory", "t").SendAsync(Raw(
            "sync/collections/notes/push",
            """{"operations":[{"operationId":"m1","documentId":"secret","baseVersion":null,"document":{"Id":"secret","UpdatedAt":"000000000001000:000000:n","Deleted":false,"Title":"mallory","Body":""}}]}"""));
        var json = await raw.Content.ReadAsStringAsync();
        var replay = (await mallory.PushAsync(new PushRequest<Note>([new("m1", "secret", null, new Note { Id = "secret", Title = "mallory", UpdatedAt = new HlcTimestamp(1_000, 0, "n") })]))).Outcomes[0];

        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Contains("\"kind\":\"rejected\"", json);
        Assert.DoesNotContain("secret\",\"UpdatedAt", json);
        Assert.DoesNotContain("alice", json);
        Assert.Equal(PushOutcomeKind.Rejected, replay.Kind);
        Assert.Null(replay.Document);
        Assert.Empty((await mallory.PullAsync(new PullRequest(Checkpoint.Start, 10))).Changes);
    }

    [Fact(DisplayName = "T18 I18: HTTP and in-process callers get identical authorization decisions")]
    public async Task HostEquivalence()
    {
        static bool OnlyOwnPrefix(SyncCallContext context, PushOperation<Note> op, Note? _) =>
            op.DocumentId.StartsWith(context.Principal.Identity?.Name ?? "?", StringComparison.Ordinal);
        var server = Server(canWrite: OnlyOwnPrefix);
        await using var host = await SyncTestHost.StartAsync(server, requireAuthentication: true);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant", "t")], "Test"));
        var inProcess = new Bsync.Server.InProcessTransport<Note>(server, new SyncCallContext(principal, "t"));
        var overHttp = host.Transport("alice", "t");

        var local = (await inProcess.PushAsync(Push(("p1", "alice-1", null), ("p2", "bob-1", null)))).Outcomes;
        var remote = (await overHttp.PushAsync(Push(("h1", "alice-2", null), ("h2", "bob-2", null)))).Outcomes;

        Assert.Equal(local.Select(o => (o.Kind, o.ErrorCode)), remote.Select(o => (o.Kind, o.ErrorCode)));
        Assert.Equal([PushOutcomeKind.Accepted, PushOutcomeKind.Rejected], remote.Select(o => o.Kind));
    }

    [Fact(DisplayName = "I07 I14: the reset reason crosses the wire (scope change and expired checkpoint)")]
    public async Task ResetReasonOverHttp()
    {
        var fingerprint = "grants-1";
        var options = NoteJson.ServerOptions();
        var server = new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
        {
            Cloner = options.Cloner,
            Fingerprint = options.Fingerprint,
            PhysicalClock = options.PhysicalClock,
            ScopeFingerprint = _ => fingerprint,
        });
        await using var host = await SyncTestHost.StartAsync(server);
        var client = host.Transport();
        await client.PushAsync(Push(("o1", "a", null)));
        var checkpoint = (await client.PullAsync(new PullRequest(Checkpoint.Start, 10))).Checkpoint;

        fingerprint = "grants-2";
        var scopeChanged = await Assert.ThrowsAsync<SyncResetRequiredException>(() => client.PullAsync(new PullRequest(checkpoint, 10)));
        Assert.Equal(ResetReasons.ScopeChanged, scopeChanged.Reason);

        var fresh = (await client.PullAsync(new PullRequest(Checkpoint.Start, 10))).Checkpoint;
        await client.PushAsync(Push(("o2", "b", null)));
        server.PurgeTombstones(server.HighestVersion);
        var expired = await Assert.ThrowsAsync<SyncResetRequiredException>(() => client.PullAsync(new PullRequest(fresh, 10)));
        Assert.Equal(ResetReasons.Expired, expired.Reason);
    }

    [Fact(DisplayName = "I08 T53: a transient server failure carries Retry-After and is classified as transient")]
    public async Task TransientFailure()
    {
        await using var host = await SyncTestHost.StartAsync(new FailingAuthority(new SyncTransportException(SyncErrorCodes.Unavailable, "maintenance", isTransient: true, TimeSpan.FromSeconds(7))));
        var error = await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport().PullAsync(new PullRequest(Checkpoint.Start, 1)));

        Assert.Equal((SyncErrorCodes.Unavailable, 503, true), (error.ErrorCode, error.StatusCode, error.IsTransient));
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
    }

    [Fact(DisplayName = "T13 I15: a network failure is transient and a caller cancellation is a cancellation")]
    public async Task NetworkFailureAndCancellation()
    {
        var offline = new HttpSyncTransport<Note>(
            new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://unreachable.invalid/") },
            new HttpSyncTransportOptions { Collection = "notes", SchemaId = SyncTestHost.SchemaId },
            SyncTestHost.Json);
        var error = await Assert.ThrowsAsync<SyncTransportException>(() => offline.PullAsync(new PullRequest(Checkpoint.Start, 1)));
        Assert.True(error.IsTransient);

        await using var host = await SyncTestHost.StartAsync(new SlowAuthority(Server(), TimeSpan.FromSeconds(10)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Transport().PullAsync(new PullRequest(Checkpoint.Start, 1), cts.Token));
    }

    [Fact(DisplayName = "T11 T15 I04 I15: a push that times out after the server committed is retried without a second effect")]
    public async Task TimeoutAfterCommitIsRetrySafe()
    {
        var server = Server();
        var slow = new SlowAuthority(server, TimeSpan.FromSeconds(2)) { DelayAfterCommit = true };
        await using var host = await SyncTestHost.StartAsync(slow);

        // Only the first push gets the short timeout. The retry must not race it on a loaded machine: it would time
        // out too, which is correct behaviour but not what this test is about.
        var transport = new SwitchableTransport(host.Transport(timeout: TimeSpan.FromMilliseconds(300)));
        var client = new TestReplica(InMemorySyncServerRef.Create(), "a", transport: _ => transport);
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "once" });

        var timeout = await Assert.ThrowsAsync<SyncTransportException>(() => client.Engine.PushAsync());
        Assert.True(timeout.IsTransient);
        slow.Delay = TimeSpan.Zero;
        transport.Current = host.Transport();
        var retry = await client.Engine.PushAsync();

        Assert.True(retry.IsComplete);
        Assert.Equal(1, server.ReceiptCount);
        Assert.Equal(1, server.HighestVersion);
    }

    [Fact(DisplayName = "T12 T35 I14 I20: replicas converge over HTTP through lost responses and an authority restore")]
    public async Task EnginesConvergeOverHttp()
    {
        var serverRef = InMemorySyncServerRef.Create();
        await using var host = await SyncTestHost.StartAsync(new RefAuthority(serverRef));
        var a = new TestReplica(serverRef, "a", transport: _ => host.Transport());
        var b = new TestReplica(serverRef, "b", transport: _ => host.Transport());

        await a.Engine.WriteAsync(new Note { Id = "n1", Title = "one" });
        await a.Engine.SyncAsync();
        var backup = serverRef.Server.CreateBackup();
        await b.Engine.SyncAsync();
        await b.Engine.WriteAsync(new Note { Id = "n2", Title = "lost after restore" });
        b.Transport.LoseResponses = 1;
        await Assert.ThrowsAsync<InjectedFaultException>(() => b.Engine.SyncAsync());
        await a.Engine.WriteAsync(new Note { Id = "n1", Title = "pending across restore" });

        serverRef.Restore(backup);
        for (var i = 0; i < 3; i++)
        {
            await a.Engine.SyncAsync();
            await b.Engine.SyncAsync();
        }

        var expected = serverRef.Server.Snapshot().ToDictionary(n => n.Id, n => n.Title);
        Assert.Equal("pending across restore", expected["n1"]);
        Assert.Equal((await a.Engine.QueryAsync()).ToDictionary(n => n.Id, n => n.Title), expected);
        Assert.Equal((await b.Engine.QueryAsync()).ToDictionary(n => n.Id, n => n.Title), expected);
    }

    private sealed class FailingAuthority(Exception error) : ISyncAuthority<Note>
    {
        public AuthorityLimits Limits { get; } = new(10, 10);

        public Task<PullResult<Note>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default) => Task.FromException<PullResult<Note>>(error);

        public Task<PushResult<Note>> PushAsync(SyncCallContext context, PushRequest<Note> request, CancellationToken cancellationToken = default) => Task.FromException<PushResult<Note>>(error);
    }

    private sealed class SlowAuthority(ISyncAuthority<Note> inner, TimeSpan delay) : ISyncAuthority<Note>
    {
        public TimeSpan Delay { get; set; } = delay;

        public bool DelayAfterCommit { get; init; }

        public AuthorityLimits Limits => inner.Limits;

        public async Task<PullResult<Note>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Delay, cancellationToken);
            return await inner.PullAsync(context, request, cancellationToken);
        }

        public async Task<PushResult<Note>> PushAsync(SyncCallContext context, PushRequest<Note> request, CancellationToken cancellationToken = default)
        {
            if (!DelayAfterCommit)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var result = await inner.PushAsync(context, request, CancellationToken.None);
            if (DelayAfterCommit)
            {
                await Task.Delay(Delay, CancellationToken.None);
            }

            return result;
        }
    }

    private sealed class SwitchableTransport(ISyncTransport<Note> initial) : ISyncTransport<Note>
    {
        public ISyncTransport<Note> Current { get; set; } = initial;

        public Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) =>
            Current.PullAsync(request, cancellationToken);

        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default) =>
            Current.PushAsync(request, cancellationToken);

        public IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) =>
            Current.StreamAsync(since, cancellationToken);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
