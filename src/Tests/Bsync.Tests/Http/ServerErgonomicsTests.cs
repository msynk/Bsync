using System.Net;
using System.Security.Claims;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Testing;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>Task B4: several collections under one policy, the caller in validation, and advertised limits.</summary>
public sealed class ServerErgonomicsTests
{
    private static readonly SyncEndpointOptions Options = new()
    {
        SupportedSchemas = new HashSet<string>([SyncTestHost.SchemaId, HttpAuthorityDriver.SchemaId], StringComparer.Ordinal),
        ResolveScope = http => http.User.FindFirst("tenant")?.Value,
    };

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder>? services, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy("sync", p => p.RequireClaim("tenant")));
        services?.Invoke(builder);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, string? tenant)
    {
        var client = app.GetTestServer().CreateClient();
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "user");
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenant);
        }

        return client;
    }

    [Fact(DisplayName = "B4 I07 I18: collections mapped together share the scope resolver and one authorization policy")]
    public async Task CollectionsShareOptionsAndPolicy()
    {
        var notes = new ScopedAuthority<Note>(_ => new InMemorySyncServer<Note>(NoteJson.ServerOptions(Clocks.SystemPhysicalClock.Instance)), new AuthorityLimits(1000, 1000));
        await using var app = await StartAsync(
            b => b.Services.AddSingleton<ISyncAuthority<ConformanceDocument>>(new InMemorySyncServer<ConformanceDocument>(new InMemorySyncServerOptions<ConformanceDocument>
            {
                Cloner = Documents.DocumentCloner.Json(ConformanceJsonContext.Default.ConformanceDocument),
                Fingerprint = Documents.DocumentCloner.JsonFingerprint(ConformanceJsonContext.Default.ConformanceDocument),
            })).AddSingleton(HttpAuthorityDriver.Json),
            a => a.MapSyncCollections(Options, group => group
                .Add("notes", notes, SyncTestHost.Json)
                .Add<ConformanceDocument>(HttpAuthorityDriver.Collection)).RequireAuthorization("sync"));

        var anonymous = await Client(app, null).PostAsync("sync/collections/notes/pull", new StringContent("{}"));
        var anonymousOther = await Client(app, null).PostAsync($"sync/collections/{HttpAuthorityDriver.Collection}/pull", new StringContent("{}"));
        var a = new HttpSyncTransport<Note>(Client(app, "tenant-a"), new HttpSyncTransportOptions { Collection = "notes", SchemaId = SyncTestHost.SchemaId }, SyncTestHost.Json);
        var b = new HttpSyncTransport<Note>(Client(app, "tenant-b"), new HttpSyncTransportOptions { Collection = "notes", SchemaId = SyncTestHost.SchemaId }, SyncTestHost.Json);
        var other = new HttpSyncTransport<ConformanceDocument>(Client(app, "tenant-a"), new HttpSyncTransportOptions { Collection = HttpAuthorityDriver.Collection, SchemaId = HttpAuthorityDriver.SchemaId }, HttpAuthorityDriver.Json);
        var stamp = new Clocks.HlcTimestamp(Clocks.SystemPhysicalClock.Instance.NowMilliseconds(), 0, "n");
        await a.PushAsync(new PushRequest<Note>([new PushOperation<Note>("o1", "n1", null, new Note { Id = "n1", UpdatedAt = stamp })]));
        await other.PushAsync(new PushRequest<ConformanceDocument>([new PushOperation<ConformanceDocument>("o1", "c1", null, new ConformanceDocument { Id = "c1", UpdatedAt = stamp })]));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousOther.StatusCode);
        Assert.Single((await a.PullAsync(new PullRequest(Checkpoint.Start, 10))).Changes);
        Assert.Empty((await b.PullAsync(new PullRequest(Checkpoint.Start, 10))).Changes); // tenant-b's scope is resolved separately
        Assert.Single((await other.PullAsync(new PullRequest(Checkpoint.Start, 10))).Changes);
    }

    [Fact(DisplayName = "B4: a collection cannot be mapped twice in one group")]
    public async Task DuplicateCollectionRefused()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(null, a => a.MapSyncCollections(Options, group => group
            .Add("notes", new InMemorySyncServer<Note>(NoteJson.ServerOptions()), SyncTestHost.Json)
            .Add("notes", new InMemorySyncServer<Note>(NoteJson.ServerOptions()), SyncTestHost.Json))));

        Assert.Contains("notes", error.Message);
    }

    [Fact(DisplayName = "B4 F5 I18: validation receives the caller")]
    public async Task ValidatorReceivesCaller()
    {
        var authority = new InMemorySyncServer<Note>(NoteJson.ServerOptions(validator: (caller, _, _) => caller.Principal.IsInRole("reader") ? PushErrorCodes.Forbidden : null));
        var reader = new SyncCallContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "reader")], "test")), "default");
        var writer = new SyncCallContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "writer")], "test")), "default");
        Note Doc(string id) => new() { Id = id, UpdatedAt = new Clocks.HlcTimestamp(1_000, 0, "n") };

        var refused = (await authority.PushAsync(reader, new PushRequest<Note>([new PushOperation<Note>("o1", "a", null, Doc("a"))]))).Outcomes.Single();
        var allowed = (await authority.PushAsync(writer, new PushRequest<Note>([new PushOperation<Note>("o2", "b", null, Doc("b"))]))).Outcomes.Single();

        Assert.Equal(PushErrorCodes.Forbidden, refused.ErrorCode);
        Assert.Equal(PushOutcomeKind.Accepted, allowed.Kind);
    }

    [Fact(DisplayName = "B4 F6 I08: the server advertises its limits and the replica clamps its batch sizes to them")]
    public async Task ReplicaClampsToAdvertisedLimits()
    {
        var options = NoteJson.ServerOptions(maxOperationsPerPush: 3);
        var server = new InMemorySyncServerRef(new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
        {
            Cloner = options.Cloner,
            Fingerprint = options.Fingerprint,
            PhysicalClock = options.PhysicalClock,
            MaxOperationsPerPush = 3,
            MaxPageSize = 2,
        }));
        var pulls = new List<int>();
        var client = new TestReplica(server, "c", options: new SyncOptions<Note> { PushBatchSize = 50, PullBatchSize = 50 }, transport: inner => new JsonWireTransport<Note>(new RecordingPulls(inner, pulls), NoteJsonContext.Default));
        for (var i = 0; i < 10; i++)
        {
            await client.Engine.WriteAsync(new Note { Id = $"n{i}", Title = "x" });
        }

        var result = await client.Engine.SyncAsync();
        var other = new TestReplica(server, "o", options: new SyncOptions<Note> { PullBatchSize = 50 }, transport: inner => new RecordingPulls(inner, pulls));
        await other.Engine.PullAsync();
        var page = await server.Server.PullAsync(SyncCallContext.Anonymous, new PullRequest(Checkpoint.Start, 1));

        Assert.True(result.IsComplete);
        Assert.Equal(10, result.Pushed);
        Assert.All(client.Transport.PushLog, push => Assert.InRange(push.Operations.Count, 1, 3));
        Assert.Equal(50, pulls[0]); // before the server is known, the configured size
        Assert.Contains(2, pulls.Skip(1));
        Assert.Equal([SyncFeatures.Groups, SyncFeatures.Limits, SyncFeatures.ServerTime, SyncFeatures.Retention], page.Features);
        Assert.Equal(new SyncLimits(3, 2), page.Limits);
    }

    private sealed class RecordingPulls(ISyncTransport<Note> inner, List<int> batches) : ISyncTransport<Note>
    {
        public Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default)
        {
            lock (batches)
            {
                batches.Add(request.BatchSize);
            }

            return inner.PullAsync(request, cancellationToken);
        }

        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default) => inner.PushAsync(request, cancellationToken);

        public IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => inner.StreamAsync(since, cancellationToken);
    }
}
