using Bsync.Client;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Storage;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>Task C3: one coordinator for many collections.</summary>
public sealed class CoordinatorTests
{
    private static readonly SyncEndpointOptions Options = new()
    {
        SupportedSchemas = new HashSet<string>([SyncTestHost.SchemaId], StringComparer.Ordinal),
        ResolveScope = http => http.User.FindFirst("tenant")?.Value,
    };

    private static InMemorySyncServer<Note> Server(ISyncWriteHandler<Note>? handler = null)
    {
        var options = NoteJson.ServerOptions(SystemPhysicalClock.Instance);
        return new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
        {
            Cloner = options.Cloner,
            Fingerprint = options.Fingerprint,
            PhysicalClock = options.PhysicalClock,
            WriteHandler = handler,
        });
    }

    private static async Task<(WebApplication App, Func<int> HintRequests, Func<int> CollectionHintRequests)> HostAsync(IReadOnlyDictionary<string, InMemorySyncServer<Note>> servers, bool multiplexed)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        var shared = 0;
        var perCollection = 0;
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value is { } path && path.EndsWith("/hints", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref path.Contains("/collections/", StringComparison.Ordinal) ? ref perCollection : ref shared);
            }

            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        if (multiplexed)
        {
            app.MapSyncCollections(Options, group =>
            {
                foreach (var (name, server) in servers)
                {
                    group.Add(name, server, SyncTestHost.Json);
                }
            }).RequireAuthorization();
        }
        else
        {
            foreach (var (name, server) in servers)
            {
                app.MapSyncCollection(name, server, SyncTestHost.Json, Options).RequireAuthorization();
            }
        }

        await app.StartAsync();
        return (app, () => Volatile.Read(ref shared), () => Volatile.Read(ref perCollection));
    }

    private static HttpClient Client(WebApplication app)
    {
        var client = app.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "alice");
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, "team");
        return client;
    }

    private static SyncSession<Note> Session(SyncCoordinator coordinator, string name, Func<ISyncTransport<Note>> transport, int priority = 0, IReadOnlyList<string>? dependsOn = null, bool hints = false) =>
        new(new SyncSessionOptions<Note>
        {
            Cloner = NoteJson.Clone,
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Note>(new InMemoryLocalStore<Note>(NoteJson.Clone), $"node-{name}")),
            CreateTransport = _ => transport(),
            Interval = TimeSpan.FromMinutes(10),
            MinBackoff = TimeSpan.FromMilliseconds(50),
            MaxBackoff = TimeSpan.FromMilliseconds(500),
            LiveHints = hints,
            Coordination = new SyncCoordination(coordinator, name, priority, dependsOn),
        });

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    [Fact(DisplayName = "C3 I13: fifteen collections share one hint stream, and a commit wakes only its collection")]
    public async Task FifteenCollectionsOneStream()
    {
        var servers = Enumerable.Range(0, 15).ToDictionary(i => $"c{i:00}", _ => Server());
        var (app, shared, perCollection) = await HostAsync(servers, multiplexed: true);
        await using var _ = app;
        var http = Client(app);
        await using var coordinator = new SyncCoordinator(new SyncCoordinatorOptions { Hints = HttpSyncHints.Multiplexed(_ => http, SyncTestHost.SchemaId) });
        var sessions = servers.Keys.Select(name => Session(coordinator, name, () => new HttpSyncTransport<Note>(http, new HttpSyncTransportOptions { Collection = name, SchemaId = SyncTestHost.SchemaId }, SyncTestHost.Json), hints: true)).ToList();
        try
        {
            foreach (var session in sessions)
            {
                await session.GetEngineAsync("alice");
            }

            await WaitUntil(() => coordinator.Status.State == SyncState.Synced && coordinator.Status.Collections.Count == 15, "all synced");
            servers["c07"].Push(new SyncCallContext(new System.Security.Claims.ClaimsPrincipal(), "team"), new PushRequest<Note>([new PushOperation<Note>("o1", "news", null, new Note { Id = "news", UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "s") })]));
            var c07 = await sessions[7].GetEngineAsync("alice");
            await WaitUntil(() => c07.GetAsync("news").Result is not null, "the hinted collection pulled the change");

            Assert.Equal(1, shared());
            Assert.Equal(0, perCollection());
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    [Fact(DisplayName = "C3 I13: against a server without the shared stream, each collection keeps its own")]
    public async Task OldServerFallsBack()
    {
        var servers = Enumerable.Range(0, 3).ToDictionary(i => $"c{i}", _ => Server());
        var (app, _, perCollection) = await HostAsync(servers, multiplexed: false);
        await using var __ = app;
        var http = Client(app);
        await using var coordinator = new SyncCoordinator(new SyncCoordinatorOptions { Hints = HttpSyncHints.Multiplexed(_ => http, SyncTestHost.SchemaId) });
        var sessions = servers.Keys.Select(name => Session(coordinator, name, () => new HttpSyncTransport<Note>(http, new HttpSyncTransportOptions { Collection = name, SchemaId = SyncTestHost.SchemaId }, SyncTestHost.Json), hints: true)).ToList();
        try
        {
            foreach (var session in sessions)
            {
                await session.GetEngineAsync("alice");
            }

            await WaitUntil(() => perCollection() >= 3, "one stream per collection");
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>Rejects (terminally) a line whose order, named in its body, is not on the orders server.</summary>
    private sealed class OrderRequired(InMemorySyncServer<Note> orders) : ISyncWriteHandler<Note>
    {
        public ValueTask<SyncWriteDecision<Note>> HandleAsync(SyncWriteContext<Note> write, CancellationToken cancellationToken) =>
            ValueTask.FromResult(orders.GetVersion(write.Submitted.Body) is null
                ? SyncWriteDecision<Note>.Reject("order-missing")
                : SyncWriteDecision<Note>.Accept(write.Submitted));
    }

    [Theory(DisplayName = "C3 I19: a child created before its parent is never rejected when the coordinator knows the dependency")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParentsPushFirst(bool declareDependency)
    {
        var orders = Server();
        var lines = Server(new OrderRequired(orders));
        await using var coordinator = new SyncCoordinator();
        await using var orderSession = Session(coordinator, "orders", () => new InProcessTransport<Note>(orders));
        await using var lineSession = Session(coordinator, "lines", () => new InProcessTransport<Note>(lines), dependsOn: declareDependency ? ["orders"] : null);
        var orderCollection = new LocalSyncCollection<Note>(orderSession, _ => Task.FromResult("alice"));
        var lineCollection = new LocalSyncCollection<Note>(lineSession, _ => Task.FromResult("alice"));
        await orderCollection.QueryAsync();
        await lineCollection.QueryAsync();
        await WaitUntil(() => coordinator.Status.State == SyncState.Synced, "started");

        // Offline: the line is written before its order. When the app is back online, the child's session runs first.
        coordinator.Pause();
        await lineCollection.SaveAsync(new Note { Id = "line-1", Body = "order-1" });
        await orderCollection.SaveAsync(new Note { Id = "order-1" });
        lineSession.Resume();
        if (declareDependency)
        {
            await WaitUntil(() => lines.GetVersion("line-1") is not null, "line accepted");
            Assert.Null((await lineCollection.GetItemStatusAsync("line-1"))!.Detail);
        }
        else
        {
            await WaitUntil(() => lineCollection.GetItemStatusAsync("line-1").Result!.State == SyncItemState.Rejected, "line rejected without the dependency");
        }

        orderSession.Resume();
    }

    [Fact(DisplayName = "C3 I19: a failing collection does not starve the others, and the aggregate status names it")]
    public async Task FailingCollectionIsNamed()
    {
        await using var coordinator = new SyncCoordinator(new SyncCoordinatorOptions { MaxConcurrency = 1 });
        var good = new[] { Server(), Server() };
        await using var a = Session(coordinator, "a", () => new InProcessTransport<Note>(good[0]));
        await using var bad = Session(coordinator, "bad", () => new FailingTransport());
        await using var b = Session(coordinator, "b", () => new InProcessTransport<Note>(good[1]));
        var notes = new[] { a, bad, b }.Select(s => new LocalSyncCollection<Note>(s, _ => Task.FromResult("alice"))).ToList();
        foreach (var collection in notes)
        {
            await collection.SaveAsync(new Note { Id = "n1" });
        }

        await WaitUntil(() => good.All(s => s.GetVersion("n1") is not null), "the healthy collections synced");
        await WaitUntil(() => coordinator.Status.State == SyncState.AttentionRequired, "attention");

        Assert.Contains("bad:", coordinator.Status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("a:", coordinator.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(SyncState.Synced, coordinator.Status.Collections["a"].State);
        Assert.Equal(1, coordinator.Status.Pending);
    }

    [Fact(DisplayName = "C3 I13: the host bridge wakes a collection from the application's own real-time hub")]
    public async Task HostBridge()
    {
        var server = Server();
        await using var coordinator = new SyncCoordinator();
        var pulls = 0;
        await using var session = Session(coordinator, "orders", () => new CountingPulls(new InProcessTransport<Note>(server), () => Interlocked.Increment(ref pulls)));
        await session.GetEngineAsync("alice");
        await WaitUntil(() => coordinator.Status.State == SyncState.Synced, "synced");
        var before = Volatile.Read(ref pulls);

        coordinator.Hint("orders"); // for example from a SignalR handler: hub.On<string>("changed", coordinator.Hint)
        coordinator.Hint("unknown");

        await WaitUntil(() => Volatile.Read(ref pulls) > before, "hinted sync");
    }

    private sealed class FailingTransport : ISyncTransport<Note>
    {
        public Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) =>
            throw new SyncTransportException(SyncErrorCodes.Forbidden, "This collection is closed.", isTransient: false);

        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default) =>
            throw new SyncTransportException(SyncErrorCodes.Forbidden, "This collection is closed.", isTransient: false);

        public IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CountingPulls(ISyncTransport<Note> inner, Action onPull) : ISyncTransport<Note>
    {
        public Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default)
        {
            onPull();
            return inner.PullAsync(request, cancellationToken);
        }

        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default) => inner.PushAsync(request, cancellationToken);

        public IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => inner.StreamAsync(since, cancellationToken);
    }
}
