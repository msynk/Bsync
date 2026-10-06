using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Bsync.Client;
using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Console;
using Bsync.Samples.Tasks.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>Task B5: the relational sample end to end, with a real SQL Server, Kestrel and SQLite replicas.</summary>
public sealed class TasksSampleTests : IAsyncLifetime
{
    private readonly string _data = Directory.CreateTempSubdirectory("bsync-tasks-").FullName;
    private SqlServerDatabase _database = null!;

    public async Task InitializeAsync() => _database = await SqlServerDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        Directory.Delete(_data, recursive: true);
    }

    private static Uri FreeAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
    }

    private Task<WebApplication> StartServerAsync(Uri address) => TasksServer.BuildAsync([], builder =>
    {
        builder.WebHost.UseUrls(address.ToString());
        builder.Configuration["ConnectionStrings:Tasks"] = _database.ConnectionString;
        builder.Logging.ClearProviders();
    });

    [Fact(DisplayName = "B5 I01 I16 I19: offline write, restart, upload, server-computed field, rejection code, and a publisher write reaching a second client")]
    public async Task EndToEnd()
    {
        var address = FreeAddress();
        var alicesData = Path.Combine(_data, "alice");

        // 1. Offline: the server is not running. The save succeeds locally and stays pending.
        await using (var offline = TasksClient.Create(address, alicesData, "alice", "team-1"))
        {
            var saved = await offline.Tasks.SaveAsync(new TaskDocument { Id = "t1", Title = "Buy milk!" });
            Assert.Equal(SyncConfirmation.SavedLocally, saved.Confirmation);
            await Assert.ThrowsAnyAsync<Exception>(() => offline.SyncNowAsync());
            Assert.Equal(SyncItemState.Pending, (await offline.Tasks.GetItemStatusAsync("t1"))!.State);
        }

        // 2. The app restarts later, when the server is up: the pending write is uploaded from the SQLite replica.
        await using var server = await StartServerAsync(address);
        await server.StartAsync();
        await using var alice = TasksClient.Create(address, alicesData, "alice", "team-1");
        await alice.SyncNowAsync(); // the session's own loop may already have uploaded it; either way it is done now

        Assert.Equal(SyncItemState.Synced, (await alice.Tasks.GetItemStatusAsync("t1"))!.State);
        Assert.Equal("buy-milk", (await alice.Tasks.GetAsync("t1"))!.Slug); // computed by the server's write handler
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't1' AND Slug = 'buy-milk' AND ChangedBy = 'alice'"));

        // 3. A rule the server enforces: the rejection code is kept on the record and visible to the UI.
        await alice.Tasks.SaveAsync(new TaskDocument { Id = "t2", Title = "   " });
        await alice.SyncNowAsync();
        var status = await alice.Tasks.GetItemStatusAsync("t2");

        Assert.Equal(SyncItemState.Rejected, status!.State);
        Assert.Equal(TaskRules.TitleRequired, status.Detail);
        Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't2'"));

        // 4. The back office creates a task through an ordinary API endpoint (EF Core and the publisher in one transaction).
        using var http = new HttpClient { BaseAddress = address };
        var token = await (await http.PostAsJsonAsync("api/token", new TokenRequest("manager", "team-1"), TasksJson.Default.TokenRequest)).Content.ReadFromJsonAsync(TasksJson.Default.TokenResponse);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token);
        var created = await http.PostAsJsonAsync("api/tasks", new NewTask("t3", "Quarterly review"), TasksJson.Default.NewTask);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // 5. A second client of the same team receives both the replicated and the published task; another team sees none.
        await using var bob = TasksClient.Create(address, Path.Combine(_data, "bob"), "bob", "team-1");
        await bob.SyncNowAsync();
        await using var eve = TasksClient.Create(address, Path.Combine(_data, "eve"), "eve", "team-2");
        await eve.SyncNowAsync();
        await alice.SyncNowAsync();

        Assert.Equal(["t1=buy-milk", "t3=quarterly-review"], (await bob.Tasks.QueryAsync()).Select(t => $"{t.Id}={t.Slug}").Order());
        Assert.Contains(await alice.Tasks.QueryAsync(), t => t.Id == "t3");
        Assert.Empty(await eve.Tasks.QueryAsync());
        Assert.Equal(2, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Tenant = 'team-1'"));
    }

    [Fact(DisplayName = "F3 I01 I16 I19: an intent stays pending across a restart, executes once despite a lost response, waits for its task, and a rejection keeps its code")]
    public async Task IntentsExecuteOnce()
    {
        var address = FreeAddress();
        var alicesData = Path.Combine(_data, "alice");
        var first = await StartServerAsync(address);
        await first.StartAsync();
        await using (var setup = TasksClient.Create(address, alicesData, "alice", "team-1"))
        {
            await setup.Tasks.SaveAsync(new TaskDocument { Id = "t1", Title = "Write report" });
            await setup.SyncNowAsync();
            Assert.Equal(1, (await setup.Tasks.GetAsync("t1"))!.Revision);
        }

        await first.StopAsync();
        await first.DisposeAsync();

        // 1. Offline, the user completes the task: the intent is saved locally and is still pending after a restart.
        string completion;
        await using (var offline = TasksClient.Create(address, alicesData, "alice", "team-1"))
        {
            completion = (await offline.CompleteAsync("t1")).Id;
            await Assert.ThrowsAnyAsync<Exception>(() => offline.SyncNowAsync());
        }

        await using (var restarted = TasksClient.Create(address, alicesData, "alice", "team-1"))
        {
            var pending = await restarted.Intents.GetAsync(completion);
            Assert.Equal((IntentStates.Pending, 1L), (pending!.State, pending.TaskRevision));
            Assert.Equal(SyncItemState.Pending, (await restarted.Intents.GetItemStatusAsync(completion))!.State);
        }

        // 2. The server is back, and the response to the intent's upload is lost after the server committed it. The
        //    replica sends the same operation again; the receipt answers it, and the task is changed once.
        await using var server = await StartServerAsync(address);
        await server.StartAsync();
        var lost = new LostResponses($"collections/{TasksJson.IntentCollection}/push");
        await using var alice = TasksClient.Create(address, alicesData, "alice", "team-1", inner => new LoseFirstResponse(lost) { InnerHandler = inner });
        for (var attempt = 0; attempt < 5 && (await alice.Intents.GetAsync(completion))!.State == IntentStates.Pending; attempt++)
        {
            try
            {
                await alice.SyncNowAsync();
            }
            catch (Exception error) when (error is SyncTransportException or HttpRequestException)
            {
                // The lost response; the next attempt resends.
            }
        }

        var executed = await alice.Intents.GetAsync(completion);
        Assert.Equal(1, lost.Count);
        Assert.Equal((IntentStates.Executed, "alice"), (executed!.State, executed.ExecutedBy));
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't1' AND Done = 1 AND Revision = 2"));
        Assert.Equal((true, 2L), ((await alice.Tasks.GetAsync("t1"))!.Done, (await alice.Tasks.GetAsync("t1"))!.Revision));

        // 3. An intent based on a revision the task no longer has is accepted but not executed; its code stays.
        var stale = new TaskIntent { Kind = IntentKinds.Rename, TaskId = "t1", TaskRevision = 1, Title = "Something else" };
        await alice.Intents.SaveAsync(stale);
        await alice.SyncNowAsync();
        await alice.SyncNowAsync();
        var rejected = await alice.Intents.GetAsync(stale.Id);

        Assert.Equal((IntentStates.Rejected, IntentCodes.TargetChanged), (rejected!.State, rejected.Code));
        Assert.Equal(SyncItemState.Synced, (await alice.Intents.GetItemStatusAsync(stale.Id))!.State);
        Assert.Equal("Write report", (await alice.Tasks.GetAsync("t1"))!.Title);

        // 4. Intents are immutable: changing one is refused with a stable code, and the server's copy is unchanged.
        rejected.TaskRevision = 2;
        await alice.Intents.SaveAsync(rejected);
        await alice.SyncNowAsync();
        var edit = await alice.Intents.GetItemStatusAsync(stale.Id);
        Assert.Equal((SyncItemState.Rejected, IntentCodes.Immutable), (edit!.State, edit.Detail));
        Assert.Equal("Write report", (await alice.Tasks.GetAsync("t1"))!.Title);

        // 5. An intent for a task that has not reached the server yet waits (retry-later), then executes.
        await using var bob = TasksClient.Create(address, Path.Combine(_data, "bob"), "bob", "team-1");
        var early = new TaskIntent { Kind = IntentKinds.Rename, TaskId = "t9", Title = "Plan offsite" };
        await bob.Intents.SaveAsync(early);
        await bob.SyncNowAsync();
        Assert.Equal(IntentStates.Pending, (await bob.Intents.GetAsync(early.Id))!.State);
        Assert.Equal(SyncItemState.Pending, (await bob.Intents.GetItemStatusAsync(early.Id))!.State);
        Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't9'"));

        await alice.Tasks.SaveAsync(new TaskDocument { Id = "t9", Title = "Offsite" });
        await alice.SyncNowAsync();
        await bob.SyncNowAsync();

        Assert.Equal((IntentStates.Executed, "bob"), ((await bob.Intents.GetAsync(early.Id))!.State, (await bob.Intents.GetAsync(early.Id))!.ExecutedBy));
        Assert.Equal("plan-offsite", (await bob.Tasks.GetAsync("t9"))!.Slug);
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't9' AND Title = 'Plan offsite' AND Revision = 2 AND ChangedBy = 'bob'"));
    }

    /// <summary>Which responses were lost; shared by the handlers of one client's transports.</summary>
    private sealed class LostResponses(string path)
    {
        public string Path { get; } = path;

        public int Count;
    }

    /// <summary>Sends the first matching request, then loses its response, as a dropped connection would.</summary>
    private sealed class LoseFirstResponse(LostResponses lost) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith(lost.Path, StringComparison.Ordinal)
                && response.IsSuccessStatusCode
                && Interlocked.CompareExchange(ref lost.Count, 1, 0) == 0)
            {
                response.Dispose();
                throw new HttpRequestException("The connection was closed before the response arrived (test).");
            }

            return response;
        }
    }

    [Fact(DisplayName = "B5 I07: sync and the API refuse callers without a bearer token")]
    public async Task RequiresBearerToken()
    {
        var address = FreeAddress();
        await using var server = await StartServerAsync(address);
        await server.StartAsync();
        using var http = new HttpClient { BaseAddress = address };

        var pull = await http.PostAsync($"sync/collections/{TasksJson.Collection}/pull", new StringContent("{}"));
        var api = await http.PostAsJsonAsync("api/tasks", new NewTask("x", "y"), TasksJson.Default.NewTask);

        Assert.Equal(HttpStatusCode.Unauthorized, pull.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }
}
