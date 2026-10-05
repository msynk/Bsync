using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bsync.Client;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Storage.Sqlite;
using Bsync.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Bsync.Samples.Tasks.Console;

/// <summary>
/// A native client of the tasks server: a SQLite replica in <c>dataDirectory</c> per user, synchronized over HTTP with a
/// bearer token. Saves never wait for the network; they upload when the server is reachable.
/// </summary>
public sealed class TasksClient : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _databasePath;
    private readonly string _user;

    private TasksClient(ServiceProvider services, string databasePath, string user)
    {
        _services = services;
        _databasePath = databasePath;
        _user = user;
    }

    /// <summary>The tasks, as the app sees them (local reads; never wait for the network).</summary>
    public ISyncCollection<TaskDocument> Tasks => _services.GetRequiredService<ISyncCollection<TaskDocument>>();

    public static TasksClient Create(Uri server, string dataDirectory, string user, string tenant)
    {
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, $"tasks-{user}.db");
        var services = new ServiceCollection();
        services.AddLocalSyncCollection<TaskDocument>(
            _ => new SyncSessionOptions<TaskDocument>
            {
                Host = "native",
                Cloner = DocumentCloner.Json(TasksJson.Default.TaskDocument),
                OpenReplica = async (_, cancellationToken) =>
                {
                    var store = await SqliteLocalStore<TaskDocument>.OpenAsync(
                        new SqliteLocalStoreOptions { DataSource = databasePath, Collection = TasksJson.Collection },
                        TasksJson.Default.TaskDocument,
                        cancellationToken);
                    var identity = await store.GetReplicaIdentityAsync(cancellationToken);
                    return new LocalReplica<TaskDocument>(store, identity.Incarnation);
                },

                // A dedicated HttpClient: a shared pipeline that rewrites error responses would hide Retry-After and the
                // protocol's problem codes from the transport.
                CreateTransport = _ => new HttpSyncTransport<TaskDocument>(
                    new HttpClient(new BearerHandler(server, user, tenant) { InnerHandler = new HttpClientHandler() }) { BaseAddress = server },
                    new HttpSyncTransportOptions { Collection = TasksJson.Collection, SchemaId = TasksJson.SchemaId },
                    SyncJsonTypes<TaskDocument>.From(TasksJson.Default)),
                Interval = TimeSpan.FromSeconds(30),
                MaxBackoff = TimeSpan.FromSeconds(30),
            },
            resolveAccount: (_, _) => Task.FromResult(user));
        return new TasksClient(services.BuildServiceProvider(), databasePath, user);
    }

    /// <summary>Synchronizes now (pull, then push) and reports what happened.</summary>
    public async Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        var session = _services.GetRequiredService<SyncSession<TaskDocument>>();
        var engine = await session.GetEngineAsync(_user, cancellationToken);
        return await engine.SyncAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteStorePool.Release(_databasePath);
    }

    /// <summary>Signs in on first use (development token endpoint) and adds the bearer token to every request.</summary>
    private sealed class BearerHandler(Uri server, string user, string tenant) : DelegatingHandler
    {
        private string? _token;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_token is null)
            {
                using var signIn = new HttpClient { BaseAddress = server };
                var response = await signIn.PostAsJsonAsync("api/token", new TokenRequest(user, tenant), TasksJson.Default.TokenRequest, cancellationToken);
                response.EnsureSuccessStatusCode();
                _token = (await response.Content.ReadFromJsonAsync(TasksJson.Default.TokenResponse, cancellationToken))!.Token;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
