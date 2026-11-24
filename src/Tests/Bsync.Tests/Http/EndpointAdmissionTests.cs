using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Server.AspNetCore.Blobs;
using Bsync.Tests.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>
/// Plan item A1: every Bsync route fails closed. Anonymous callers are refused with 401 unless the application opts in,
/// even when no authorization policy is put on the route group, and a scope shared by every caller is logged.
/// </summary>
public sealed class EndpointAdmissionTests
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    private sealed class Logs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }
    }

    private sealed class OpenAccess : IBlobAccess
    {
        public Task<bool> HoldsAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task RecordAsync(SyncCallContext caller, string sha256, long size, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> CanReadAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>A host with authentication but deliberately no authorization policy on any Bsync route.</summary>
    private static async Task<WebApplication> StartAsync(SyncEndpointOptions options, Logs? logs = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        var app = builder.Build();
        app.UseAuthentication();
        var notes = InMemorySyncServerRef.Create().Server;
        app.MapSyncCollection("notes", notes, SyncTestHost.Json, options);
        app.MapSyncCollections(options, group => group.Add("todos", InMemorySyncServerRef.Create().Server, SyncTestHost.Json), prefix: "grouped");
        app.MapSyncBlobs(new FileSystemBlobStore(Directory.CreateTempSubdirectory("bsync-admission-").FullName), new OpenAccess(), options);
        await app.StartAsync();
        return app;
    }

    private static SyncEndpointOptions Options(bool allowAnonymous = false, Func<Microsoft.AspNetCore.Http.HttpContext, string?>? scope = null) => new()
    {
        SupportedSchemas = new HashSet<string>([SyncTestHost.SchemaId], StringComparer.Ordinal),
        ResolveScope = scope ?? (http => http.User.FindFirst("tenant")?.Value),
        AllowAnonymous = allowAnonymous,
    };

    private static HttpClient Client(WebApplication app, string? tenant)
    {
        var client = app.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("Bsync-Protocol", "1");
        client.DefaultRequestHeaders.Add("Bsync-Schema", SyncTestHost.SchemaId);
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "user");
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenant);
        }

        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>One request per route of every route group, each answered without waiting for a hint stream to end.</summary>
    private static async Task<Dictionary<string, HttpStatusCode>> CallEveryRouteAsync(HttpClient client)
    {
        async Task<HttpStatusCode> Send(HttpRequestMessage request)
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.StatusCode;
        }

        return new Dictionary<string, HttpStatusCode>
        {
            ["pull"] = await Send(new(HttpMethod.Post, "sync/collections/notes/pull") { Content = Json("""{"checkpoint":null,"limit":10}""") }),
            ["push"] = await Send(new(HttpMethod.Post, "sync/collections/notes/push") { Content = Json("""{"operations":[]}""") }),
            ["hints"] = await Send(new(HttpMethod.Get, "sync/collections/notes/hints")),
            ["group pull"] = await Send(new(HttpMethod.Post, "grouped/collections/todos/pull") { Content = Json("""{"checkpoint":null,"limit":10}""") }),
            ["group batch"] = await Send(new(HttpMethod.Post, "grouped/pull") { Content = Json("""{"collections":{}}""") }),
            ["group hints"] = await Send(new(HttpMethod.Get, "grouped/hints?collections=todos")),
            ["blob start"] = await Send(new(HttpMethod.Post, $"sync/blobs/{Hash}/uploads?size=1")),
            ["blob append"] = await Send(new(HttpMethod.Put, $"sync/blobs/{Hash}/uploads/0") { Content = new ByteArrayContent([1]) }),
            ["blob finish"] = await Send(new(HttpMethod.Post, $"sync/blobs/{Hash}/uploads/finish")),
            ["blob ticket"] = await Send(new(HttpMethod.Get, $"sync/blobs/{Hash}/download")),
            ["blob read"] = await Send(new(HttpMethod.Get, $"sync/blobs/{Hash}")),
        };
    }

    [Fact(DisplayName = "A1: without an authorization policy, every Bsync route refuses an anonymous caller with 401")]
    public async Task AnonymousCallersAreRefusedEverywhere()
    {
        await using var app = await StartAsync(Options(scope: SyncEndpointOptions.SharedScope()));

        var statuses = await CallEveryRouteAsync(Client(app, tenant: null));

        Assert.All(statuses, route => Assert.True(route.Value == HttpStatusCode.Unauthorized, $"{route.Key}: {route.Value}"));
    }

    [Fact(DisplayName = "A1: an authenticated caller passes admission on every Bsync route")]
    public async Task AuthenticatedCallersAreAdmitted()
    {
        await using var app = await StartAsync(Options());

        var statuses = await CallEveryRouteAsync(Client(app, tenant: "tenant-a"));

        Assert.All(statuses, route => Assert.True(route.Value is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{route.Key}: {route.Value}"));
        Assert.Equal(HttpStatusCode.OK, statuses["pull"]);
        Assert.Equal(HttpStatusCode.OK, statuses["blob start"]);
    }

    [Fact(DisplayName = "A1: AllowAnonymous lets anonymous callers reach the scope resolver")]
    public async Task AnonymousCallersAreAdmittedWhenAllowed()
    {
        await using var app = await StartAsync(Options(allowAnonymous: true, scope: SyncEndpointOptions.SharedScope("public")));

        var statuses = await CallEveryRouteAsync(Client(app, tenant: null));

        Assert.All(statuses, route => Assert.True(route.Value is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{route.Key}: {route.Value}"));
    }

    [Fact(DisplayName = "A1: an authenticated caller without a scope is still refused with 403")]
    public async Task CallersWithoutScopeAreForbidden()
    {
        await using var app = await StartAsync(Options(scope: _ => null));

        var response = await Client(app, "tenant-a").PostAsync("sync/collections/notes/pull", Json("""{"checkpoint":null,"limit":10}"""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(DisplayName = "A1: mapping routes with a scope shared by every caller logs a warning once per mapping")]
    public async Task SharedScopeIsLogged()
    {
        var logs = new Logs();
        await using var app = await StartAsync(Options(allowAnonymous: true, scope: SyncEndpointOptions.SharedScope("everyone")), logs);

        var warnings = logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("'everyone'", StringComparison.Ordinal)).Select(e => e.Message).ToList();

        Assert.Equal(3, warnings.Count); // the single collection, the group, the blob routes
        Assert.Contains(warnings, m => m.Contains("sync/collections/notes", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("grouped", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("sync/blobs", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "A1: a per-caller scope resolver logs no shared-scope warning")]
    public async Task PerCallerScopeIsNotLogged()
    {
        var logs = new Logs();
        await using var app = await StartAsync(Options(), logs);

        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("shares the scope", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "A1: a shared scope must be a valid identifier")]
    [InlineData("")]
    [InlineData("tab	here")]
    public void SharedScopeIsValidated(string scope) =>
        Assert.Throws<ArgumentException>(() => SyncEndpointOptions.SharedScope(scope));
}
