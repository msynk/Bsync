using System.Net;
using System.Text;
using System.Text.Json;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>
/// Plan item A6: a pull batch refuses repeated collection names and more parts than the group maps, runs a bounded
/// number of authority pulls at a time, and streams its parts.
/// </summary>
public sealed class PullBatchHardeningTests
{
    private const string Pull = """{"checkpoint":null,"limit":10}""";

    /// <summary>Counts concurrent pulls of the wrapped authority.</summary>
    private sealed class Counting(ISyncAuthority<Note> inner, Gate gate) : ISyncAuthority<Note>
    {
        public AuthorityLimits Limits => inner.Limits;

        public async Task<PullResult<Note>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default)
        {
            gate.Enter();
            try
            {
                await Task.Delay(20, cancellationToken);
                return await inner.PullAsync(context, request, cancellationToken);
            }
            finally
            {
                gate.Exit();
            }
        }

        public Task<PushResult<Note>> PushAsync(SyncCallContext context, PushRequest<Note> request, CancellationToken cancellationToken = default) =>
            inner.PushAsync(context, request, cancellationToken);
    }

    private sealed class Gate
    {
        private int _current;

        public int Calls;

        public int Peak;

        public void Enter()
        {
            Interlocked.Increment(ref Calls);
            var now = Interlocked.Increment(ref _current);
            int peak;
            while (now > (peak = Volatile.Read(ref Peak)) && Interlocked.CompareExchange(ref Peak, now, peak) != peak)
            {
            }
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private const int Concurrency = 2;

    private static async Task<(WebApplication App, Gate Gate)> StartAsync(int collections)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var gate = new Gate();
        app.MapSyncCollections(
            new SyncEndpointOptions
            {
                SupportedSchemas = new HashSet<string>([SyncTestHost.SchemaId], StringComparer.Ordinal),
                ResolveScope = SyncEndpointOptions.SharedScope(),
                AllowAnonymous = true,
                MaxConcurrentBatchPulls = Concurrency,
            },
            group =>
            {
                for (var i = 0; i < collections; i++)
                {
                    group.Add($"c{i}", new Counting(InMemorySyncServerRef.Create().Server, gate), SyncTestHost.Json);
                }
            });
        await app.StartAsync();
        return (app, gate);
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app, string body)
    {
        var client = app.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("Bsync-Protocol", "1");
        client.DefaultRequestHeaders.Add("Bsync-Schema", SyncTestHost.SchemaId);
        return await client.PostAsync("sync/pull", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    /// <summary>A batch body with one start pull per name, names repeated as given.</summary>
    private static string Batch(params string[] names) =>
        "{\"collections\":{" + string.Join(",", names.Select(name => $"\"{name}\":{Pull}")) + "}}";

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact(DisplayName = "A6: a pull batch naming a collection twice is refused with 400 before any pull runs")]
    public async Task RepeatedNamesAreRefused()
    {
        var (app, gate) = await StartAsync(collections: 2);
        await using var _ = app;

        var response = await PostAsync(app, Batch("c0", "c0", "c1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SyncErrorCodes.InvalidRequest, await CodeAsync(response));
        Assert.Equal(0, gate.Calls);
    }

    [Fact(DisplayName = "A6: a pull batch with more parts than the group maps is refused with 400")]
    public async Task TooManyPartsAreRefused()
    {
        var (app, gate) = await StartAsync(collections: 2);
        await using var _ = app;

        var response = await PostAsync(app, Batch("c0", "c1", "unknown"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, gate.Calls);
    }

    [Fact(DisplayName = "A6: a pull batch runs a bounded number of authority pulls at a time and answers every part")]
    public async Task PullsAreBounded()
    {
        const int collections = 3 * Concurrency;
        var (app, gate) = await StartAsync(collections);
        await using var _ = app;
        var response = await PostAsync(app, Batch([.. Enumerable.Range(0, collections).Select(i => $"c{i}")]));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(collections, gate.Calls);
        Assert.InRange(gate.Peak, 1, Concurrency);
        var results = body.RootElement.GetProperty("results").EnumerateObject().ToList();
        Assert.Equal(Enumerable.Range(0, collections).Select(i => $"c{i}"), results.Select(r => r.Name));
        Assert.All(results, r => Assert.Equal(200, r.Value.GetProperty("status").GetInt32()));
    }

    [Fact(DisplayName = "A6: an unknown collection within the limit is still answered per part with 404")]
    public async Task UnknownCollectionIsAPartProblem()
    {
        var (app, _) = await StartAsync(collections: 2);
        await using var __ = app;

        var response = await PostAsync(app, Batch("c0", "unknown"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(404, body.RootElement.GetProperty("results").GetProperty("unknown").GetProperty("status").GetInt32());
    }
}
