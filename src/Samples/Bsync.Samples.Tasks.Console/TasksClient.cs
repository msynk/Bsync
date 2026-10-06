using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
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

    /// <summary>The intents this user created, with the server's execution state once synced (task F3).</summary>
    public ISyncCollection<TaskIntent> Intents => _services.GetRequiredService<ISyncCollection<TaskIntent>>();

    /// <param name="server">The sample server.</param>
    /// <param name="dataDirectory">Where the SQLite replica lives.</param>
    /// <param name="user">The user to sign in as (development tokens).</param>
    /// <param name="tenant">The user's team.</param>
    /// <param name="wrapHandler">Wraps the HTTP handler of the sync transport (diagnostics, tests).</param>
    public static TasksClient Create(Uri server, string dataDirectory, string user, string tenant, Func<HttpMessageHandler, HttpMessageHandler>? wrapHandler = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, $"tasks-{user}.db");
        var services = new ServiceCollection();
        Add(services, TasksJson.Collection, TasksJson.Default.TaskDocument);
        Add(services, TasksJson.IntentCollection, TasksJson.Default.TaskIntent);
        return new TasksClient(services.BuildServiceProvider(), databasePath, user);

        // Both collections share the database file; each has its own rows, cursor and push queue.
        void Add<TDocument>(ServiceCollection services, string collection, JsonTypeInfo<TDocument> type)
            where TDocument : class, ISyncEntity
        {
            services.AddLocalSyncCollection<TDocument>(
                _ => new SyncSessionOptions<TDocument>
                {
                    Host = "native",
                    Cloner = DocumentCloner.Json(type),
                    OpenReplica = async (_, cancellationToken) =>
                    {
                        var store = await SqliteLocalStore<TDocument>.OpenAsync(
                            new SqliteLocalStoreOptions { DataSource = databasePath, Collection = collection },
                            type,
                            cancellationToken);
                        var identity = await store.GetReplicaIdentityAsync(cancellationToken);
                        return new LocalReplica<TDocument>(store, identity.Incarnation);
                    },

                    // A dedicated HttpClient: a shared pipeline that rewrites error responses would hide Retry-After and
                    // the protocol's problem codes from the transport.
                    CreateTransport = _ =>
                    {
                        HttpMessageHandler handler = new BearerHandler(server, user, tenant) { InnerHandler = new HttpClientHandler() };
                        return new HttpSyncTransport<TDocument>(
                            new HttpClient(wrapHandler?.Invoke(handler) ?? handler) { BaseAddress = server },
                            new HttpSyncTransportOptions { Collection = collection, SchemaId = TasksJson.SchemaId },
                            SyncJsonTypes<TDocument>.From(TasksJson.Default));
                    },
                    Interval = TimeSpan.FromSeconds(30),
                    MaxBackoff = TimeSpan.FromSeconds(30),
                },
                resolveAccount: (_, _) => Task.FromResult(user));
        }
    }

    /// <summary>Asks the server to mark a task done, if it is still at the revision the user saw.</summary>
    public async Task<TaskIntent> CompleteAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var task = await Tasks.GetAsync(taskId, cancellationToken);
        var intent = new TaskIntent { Kind = IntentKinds.Complete, TaskId = taskId, TaskRevision = task?.Revision };
        await Intents.SaveAsync(intent, cancellationToken);
        return intent;
    }

    /// <summary>Asks the server to rename a task, if it is still at the revision the user saw.</summary>
    public async Task<TaskIntent> RenameAsync(string taskId, string title, CancellationToken cancellationToken = default)
    {
        var task = await Tasks.GetAsync(taskId, cancellationToken);
        var intent = new TaskIntent { Kind = IntentKinds.Rename, TaskId = taskId, TaskRevision = task?.Revision, Title = title };
        await Intents.SaveAsync(intent, cancellationToken);
        return intent;
    }

    /// <summary>
    /// Synchronizes now and reports what happened: tasks first (an intent may name a task created on this device), then
    /// intents, then tasks again (to receive what the intents changed).
    /// </summary>
    public async Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await _services.GetRequiredService<SyncSession<TaskDocument>>().GetEngineAsync(_user, cancellationToken);
        var intents = await _services.GetRequiredService<SyncSession<TaskIntent>>().GetEngineAsync(_user, cancellationToken);
        var result = await tasks.SyncAsync(cancellationToken);
        result += await intents.SyncAsync(cancellationToken);
        return result + await tasks.SyncAsync(cancellationToken);
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
