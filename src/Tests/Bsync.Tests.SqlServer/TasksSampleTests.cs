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
