using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Blobs;
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
    private readonly LocalBlobStore _blobs;
    private readonly HttpClient _blobHttp;
    private readonly HttpClient _directHttp;
    private readonly HttpBlobTransfer _transfer;

    private TasksClient(ServiceProvider services, string databasePath, string user, LocalBlobStore blobs, HttpClient blobHttp, HttpClient directHttp)
    {
        _services = services;
        _databasePath = databasePath;
        _user = user;
        _blobs = blobs;
        _blobHttp = blobHttp;
        _directHttp = directHttp;
        _transfer = new HttpBlobTransfer(blobHttp, new HttpBlobTransferOptions { DirectClient = directHttp });
    }

    /// <summary>The tasks, as the app sees them (local reads; never wait for the network).</summary>
    public ISyncCollection<TaskDocument> Tasks => _services.GetRequiredService<ISyncCollection<TaskDocument>>();

    /// <summary>The intents this user created, with the server's execution state once synced (task F3).</summary>
    public ISyncCollection<TaskIntent> Intents => _services.GetRequiredService<ISyncCollection<TaskIntent>>();

    /// <summary>
    /// The latest bundle manifests the device knows of (task F2; read-only). Use <see cref="GetActiveBundle"/> for the
    /// revision to show.
    /// </summary>
    public ISyncCollection<BundleManifest> Bundles => _services.GetRequiredService<ISyncCollection<BundleManifest>>();

    /// <param name="server">The sample server.</param>
    /// <param name="dataDirectory">Where the SQLite replica lives.</param>
    /// <param name="user">The user to sign in as (development tokens).</param>
    /// <param name="tenant">The user's team.</param>
    /// <param name="wrapHandler">Wraps the HTTP handler of the sync transport (diagnostics, tests).</param>
    public static TasksClient Create(Uri server, string dataDirectory, string user, string tenant, Func<HttpMessageHandler, HttpMessageHandler>? wrapHandler = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, $"tasks-{user}.db");
        var blobs = new LocalBlobStore(Path.Combine(dataDirectory, $"blobs-{user}"));
        var services = new ServiceCollection();

        // A task is sent only once the server holds every attachment this device added to it (task F1). Content that is
        // not on this device came from the server (or another device's upload); the server checks those itself.
        Add(services, TasksJson.Collection, TasksJson.Default.TaskDocument, new SyncOptions<TaskDocument>
        {
            ReadyToPush = (task, _) => ValueTask.FromResult(task.Deleted || task.Attachments.All(a => blobs.IsUploaded(a) || !blobs.Has(a))),
        });
        Add(services, TasksJson.IntentCollection, TasksJson.Default.TaskIntent, null);
        Add(services, TasksJson.BundleCollection, TasksJson.Default.BundleManifest, new SyncOptions<BundleManifest> { Mode = SyncMode.PullOnly });
        HttpMessageHandler blobHandler = new BearerHandler(server, user, tenant) { InnerHandler = new HttpClientHandler() };
        var blobHttp = new HttpClient(wrapHandler?.Invoke(blobHandler) ?? blobHandler) { BaseAddress = server, Timeout = TimeSpan.FromMinutes(10) };
        // Presigned object-store URLs carry their own authorization: no bearer token there.
        var directHttp = new HttpClient(wrapHandler?.Invoke(new HttpClientHandler()) ?? new HttpClientHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        return new TasksClient(services.BuildServiceProvider(), databasePath, user, blobs, blobHttp, directHttp);

        // Both collections share the database file; each has its own rows, cursor and push queue.
        void Add<TDocument>(ServiceCollection services, string collection, JsonTypeInfo<TDocument> type, SyncOptions<TDocument>? engineOptions)
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
                    EngineOptions = engineOptions,
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
    /// Attaches content to a task (task F1): the bytes are copied into the device's blob store and flushed before the
    /// task is saved with the reference, so a saved reference always has its bytes. The upload happens on the next sync,
    /// before the task itself is sent.
    /// </summary>
    public async Task<BlobReference> AttachAsync(string taskId, Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var task = await Tasks.GetAsync(taskId, cancellationToken) ?? throw new InvalidOperationException($"No task '{taskId}'.");
        var blob = await _blobs.ImportAsync(content, fileName, contentType, cancellationToken);
        task.Attachments.Add(blob);
        await Tasks.SaveAsync(task, cancellationToken);
        return blob;
    }

    /// <summary>
    /// Opens an attachment's verified bytes, or returns <see langword="null"/> when they are not on this device: the task
    /// stays readable, and the attachment is shown as unavailable until <see cref="DownloadAsync"/> succeeds.
    /// </summary>
    public Stream? OpenAttachment(BlobReference attachment) => _blobs.TryOpenRead(attachment);

    /// <summary>Downloads an attachment, continuing an interrupted download. Returns the bytes received by this call.</summary>
    public async Task<long> DownloadAsync(BlobReference attachment, CancellationToken cancellationToken = default)
    {
        if (_blobs.Has(attachment))
        {
            return 0;
        }

        var received = await _transfer.DownloadAsync(_blobs.Content, attachment.Sha256, attachment.Size, cancellationToken);
        _blobs.MarkUploaded(attachment); // it came from the server
        return received;
    }

    /// <summary>
    /// Stores content locally and uploads it, for the back office to reference (for example in a bundle). Returns its
    /// reference once the server holds it.
    /// </summary>
    public async Task<BlobReference> UploadContentAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var blob = await _blobs.ImportAsync(content, fileName, contentType, cancellationToken);
        if (!_blobs.IsUploaded(blob))
        {
            await _transfer.UploadAsync(_blobs.Content, blob.Sha256, cancellationToken);
            _blobs.MarkUploaded(blob);
        }

        return blob;
    }

    /// <summary>
    /// The bundle revision in use: complete and verified on this device. Read every item through this one manifest, so all
    /// of them come from the same revision.
    /// </summary>
    public BundleManifest? GetActiveBundle(string bundleId) => _blobs.ReadActiveBundle(bundleId);

    /// <summary>What the device holds of a bundle: the revision in use, the latest known, and what is still missing.</summary>
    public async Task<BundleState> GetBundleStateAsync(string bundleId, CancellationToken cancellationToken = default)
    {
        var latest = await Bundles.GetAsync(bundleId, cancellationToken);
        var missing = latest?.Items.Where(i => !_blobs.Has(i)).ToList() ?? [];
        return new BundleState(
            bundleId,
            _blobs.ReadActiveBundle(bundleId)?.Revision,
            latest?.Revision,
            [.. missing.Select(i => i.FileName)],
            missing.Sum(i => i.Size - _blobs.PartialLength(i)));
    }

    /// <summary>
    /// Downloads the items of every bundle revision newer than the one in use and switches to it once all are verified.
    /// An interrupted update leaves the previous revision in use; the next call continues where this one stopped.
    /// </summary>
    public async Task UpdateBundlesAsync(CancellationToken cancellationToken = default)
    {
        var engine = await _services.GetRequiredService<SyncSession<BundleManifest>>().GetEngineAsync(_user, cancellationToken);
        var latest = (await engine.QueryAsync(cancellationToken: cancellationToken)).ToDictionary(b => b.Id, StringComparer.Ordinal);
        foreach (var active in _blobs.ActiveBundles().Where(a => !latest.ContainsKey(a.Id)))
        {
            _blobs.DeactivateBundle(active.Id);
        }

        foreach (var bundle in latest.Values)
        {
            if (_blobs.ReadActiveBundle(bundle.Id)?.Revision >= bundle.Revision)
            {
                continue;
            }

            foreach (var item in bundle.Items)
            {
                await DownloadAsync(item, cancellationToken);
            }

            _blobs.ActivateBundle(bundle);
        }
    }

    /// <summary>Pins a task: its attachments are downloaded on every sync and never evicted.</summary>
    public void Pin(string taskId) => _blobs.Pin(taskId);

    /// <summary>
    /// Removes least-recently-used attachment bytes down to <paramref name="maxBytes"/>, keeping those of pinned tasks and
    /// of tasks with local changes (which still have to upload them).
    /// </summary>
    public async Task<long> EvictAsync(long maxBytes, CancellationToken cancellationToken = default)
    {
        var pinned = _blobs.PinnedTasks();
        var keep = new HashSet<string>(StringComparer.Ordinal);

        // Bundles: the revision in use and the one being downloaded.
        keep.UnionWith(_blobs.ActiveBundles().SelectMany(b => b.Items).Select(i => i.Sha256));
        keep.UnionWith((await Bundles.QueryAsync(cancellationToken: cancellationToken)).SelectMany(b => b.Items).Select(i => i.Sha256));
        foreach (var task in await AllTasksAsync(cancellationToken))
        {
            if (pinned.Contains(task.Id) || (await Tasks.GetItemStatusAsync(task.Id, cancellationToken))?.State != SyncItemState.Synced)
            {
                keep.UnionWith(task.Attachments.Select(a => a.Sha256));
            }
        }

        return _blobs.Evict(maxBytes, keep);
    }

    /// <summary>
    /// Synchronizes now and reports what happened: attachments of local tasks are uploaded first (a task waits for its
    /// attachments), then tasks (an intent may name a task created on this device), then intents, then tasks again (to
    /// receive what the intents changed); finally bundles are pulled and updated, and the attachments of pinned tasks
    /// are downloaded.
    /// </summary>
    public async Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await _services.GetRequiredService<SyncSession<TaskDocument>>().GetEngineAsync(_user, cancellationToken);
        var intents = await _services.GetRequiredService<SyncSession<TaskIntent>>().GetEngineAsync(_user, cancellationToken);
        await UploadAttachmentsAsync(cancellationToken);
        var result = await tasks.SyncAsync(cancellationToken);
        result += await intents.SyncAsync(cancellationToken);
        result += await tasks.SyncAsync(cancellationToken);
        var bundles = await _services.GetRequiredService<SyncSession<BundleManifest>>().GetEngineAsync(_user, cancellationToken);
        result += await bundles.SyncAsync(cancellationToken);
        await UpdateBundlesAsync(cancellationToken);
        await PrefetchAsync(cancellationToken);
        return result;
    }

    /// <summary>Uploads the attachments of local tasks that the server does not hold yet. Returns the bytes sent.</summary>
    public async Task<long> UploadAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        var sent = 0L;
        foreach (var blob in (await AllTasksAsync(cancellationToken)).SelectMany(t => t.Attachments).DistinctBy(a => a.Sha256))
        {
            if (!_blobs.IsUploaded(blob) && _blobs.Has(blob))
            {
                sent += await _transfer.UploadAsync(_blobs.Content, blob.Sha256, cancellationToken);
                _blobs.MarkUploaded(blob);
            }
        }

        return sent;
    }

    private async Task PrefetchAsync(CancellationToken cancellationToken)
    {
        var pinned = _blobs.PinnedTasks();
        foreach (var task in (await AllTasksAsync(cancellationToken)).Where(t => pinned.Contains(t.Id)))
        {
            foreach (var attachment in task.Attachments)
            {
                await DownloadAsync(attachment, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<TaskDocument>> AllTasksAsync(CancellationToken cancellationToken) =>
        await (await _services.GetRequiredService<SyncSession<TaskDocument>>().GetEngineAsync(_user, cancellationToken)).QueryAsync(cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _blobHttp.Dispose();
        _directHttp.Dispose();
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
