using System.Collections.Concurrent;
using Bsync.Blazor.Blobs;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Microsoft.JSInterop;

namespace Bsync.Samples.Tasks.Client;

/// <summary>
/// Attachments in the browser (task F1): verified bytes in IndexedDB (<see cref="BrowserBlobStore"/>, one store per
/// server/tenant/user account), moved by <see cref="HttpBlobTransfer"/> through durable jobs. A task is pushed only once the server holds every
/// attachment this device added to it (<see cref="EnsureUploadedAsync"/> is the collection's <c>ReadyToPush</c>), so other
/// devices never see a task whose attachment cannot be downloaded.
/// </summary>
public sealed class TaskAttachments(SignIn session, Uri server) : IAsyncDisposable
{
    /// <summary>The largest file the app attaches.</summary>
    public const long MaxSize = 50L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, bool> _uploaded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _credentialChecks = new(StringComparer.Ordinal);
    private ManagedBlobCache? _store;
    private HttpBlobTransfer? _transfer;
    private BlobTransferManager? _jobs;
    private IndexedDbOrchestrationStore? _metadata;
    private BrowserBlobCacheLock? _locks;
    private HttpClient? _http;

    public event Action? Changed;

    /// <summary>Opens the signed-in user's store. Call after signing in, before the tasks collection syncs.</summary>
    public async Task OpenAsync(IJSRuntime js)
    {
        var bytes = await BrowserBlobStore.OpenAsync(js, $"bsync-blobs-{session.Account.StorageKey}");
        _metadata = await IndexedDbOrchestrationStore.OpenAsync(js, session.Account);
        _locks = await BrowserBlobCacheLock.OpenAsync(js, session.Account);
        _store = new(bytes, _metadata, _locks, 128 * 1024 * 1024, 256 * 1024 * 1024);
        await _store.ReconcileAsync();
        _http = new HttpClient(new BearerHandler(session) { InnerHandler = new HttpClientHandler() }) { BaseAddress = server };
        _transfer = new HttpBlobTransfer(_http, new() { DownloadTickets = true });
        _jobs = new(_metadata, _store, _transfer);
        _jobs.Changed += () => Changed?.Invoke();
        _uploaded.Clear();
        _credentialChecks.Clear();
    }

    private ManagedBlobCache Store => _store ?? throw new InvalidOperationException("Sign in first.");

    /// <summary>Stores a file on this device, verified, and returns the reference to save in the task.</summary>
    public async Task<BlobReference> ImportAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var id = Guid.CreateVersion7().ToString();
        // Browser file streams are not seekable. This sample's explicit 50 MiB file bound also bounds staging memory.
        using var input = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = await content.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (input.Length + count > MaxSize) throw new IOException("Attachment exceeds the sample's 50 MiB limit.");
            await input.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        input.Position = 0;
        var (sha256, size) = await Store.ImportPinnedAsync(input, "attachment/" + id, cancellationToken);
        return new BlobReference(id, sha256, size, contentType, fileName);
    }

    /// <summary>Whether the verified content is on this device.</summary>
    public async Task<bool> IsLocalAsync(BlobReference attachment) => await Store.SizeAsync(attachment.Sha256) == attachment.Size;

    /// <summary>Downloads an attachment, continuing an interrupted download.</summary>
    public async Task DownloadAsync(BlobReference attachment, CancellationToken cancellationToken = default)
    {
        var job = await _jobs!.EnqueueAsync(attachment.Sha256, attachment.Size, BlobTransferDirection.Download, cancellationToken: cancellationToken);
        // An explicit Download click retries unavailable/failed content; a worker already running keeps ownership.
        if (job.State != BlobTransferState.Running)
            await _jobs.SetStateAsync(attachment.Sha256, BlobTransferDirection.Download, BlobTransferState.Queued, cancellationToken);
        await _jobs.DrainAsync(TimeSpan.FromMinutes(2), cancellationToken);
        if ((await _jobs.GetAsync(attachment.Sha256, BlobTransferDirection.Download, cancellationToken))?.State != BlobTransferState.Complete)
            throw new IOException("Download is incomplete; its durable job and partial content are retained.");
        _uploaded[attachment.Sha256] = true; // it came from the server
    }

    public async Task<BlobTransferState?> UploadStateAsync(BlobReference attachment) =>
        (await _jobs!.GetAsync(attachment.Sha256, BlobTransferDirection.Upload))?.State;

    /// <summary>Opens the verified content, or returns <see langword="null"/> when it is not on this device.</summary>
    public Task<Stream?> OpenReadAsync(BlobReference attachment) => Store.OpenReadAsync(attachment.Sha256);

    /// <summary>
    /// Uploads the attachments of <paramref name="task"/> that this device holds and the server may not, and returns
    /// whether the task may be pushed. Offline (or on any transfer failure) the task waits, and the next sync tries again.
    /// </summary>
    public async ValueTask<bool> EnsureUploadedAsync(TaskDocument task, CancellationToken cancellationToken)
    {
        if (task.Deleted || _store is null || _transfer is null)
        {
            return task.Deleted || task.Attachments.Count == 0;
        }

        var prerequisites = new List<BlobReference>();
        foreach (var attachment in task.Attachments)
        {
            if (_uploaded.ContainsKey(attachment.Sha256) || await _store.SizeAsync(attachment.Sha256, cancellationToken) is null)
            {
                continue; // known to be on the server, or not on this device (it came from the server)
            }

            prerequisites.Add(attachment);
            // OpenAsync follows successful authentication. Recheck each identity once per credential lifetime,
            // so a newly expired token cannot create a retry loop and a concurrent pause/cancel remains authoritative.
            if (_credentialChecks.TryAdd(attachment.Sha256, true))
            {
                try { await _jobs!.ResumeCredentialWaitAsync(attachment.Sha256, BlobTransferDirection.Upload, cancellationToken); }
                catch { _credentialChecks.TryRemove(attachment.Sha256, out _); throw; }
            }
        }
        return await _jobs!.EnsureUploadsAsync(prerequisites, TimeSpan.FromSeconds(20), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_metadata is not null) await _metadata.DisposeAsync();
        if (_locks is not null) await _locks.DisposeAsync();
    }
}
