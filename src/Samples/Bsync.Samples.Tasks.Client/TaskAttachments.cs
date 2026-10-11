using System.Collections.Concurrent;
using Bsync.Blazor.Blobs;
using Bsync.Blobs;
using Microsoft.JSInterop;

namespace Bsync.Samples.Tasks.Client;

/// <summary>
/// Attachments in the browser (task F1): verified bytes in IndexedDB (<see cref="BrowserBlobStore"/>, one store per
/// signed-in user), moved by <see cref="HttpBlobTransfer"/>. A task is pushed only once the server holds every
/// attachment this device added to it (<see cref="EnsureUploadedAsync"/> is the collection's <c>ReadyToPush</c>), so other
/// devices never see a task whose attachment cannot be downloaded.
/// </summary>
public sealed class TaskAttachments(SignIn session, Uri server)
{
    /// <summary>The largest file the app attaches.</summary>
    public const long MaxSize = 50L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, bool> _uploaded = new(StringComparer.Ordinal);
    private BrowserBlobStore? _store;
    private HttpBlobTransfer? _transfer;

    /// <summary>Opens the signed-in user's store. Call after signing in, before the tasks collection syncs.</summary>
    public async Task OpenAsync(IJSRuntime js)
    {
        _store = await BrowserBlobStore.OpenAsync(js, $"bsync-blobs-{session.User}");
        _transfer = new HttpBlobTransfer(new HttpClient(new BearerHandler(session) { InnerHandler = new HttpClientHandler() }) { BaseAddress = server });
        _uploaded.Clear();
    }

    private BrowserBlobStore Store => _store ?? throw new InvalidOperationException("Sign in first.");

    /// <summary>Stores a file on this device, verified, and returns the reference to save in the task.</summary>
    public async Task<BlobReference> ImportAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var (sha256, size) = await Store.ImportAsync(content, cancellationToken);
        return new BlobReference(Guid.CreateVersion7().ToString(), sha256, size, contentType, fileName);
    }

    /// <summary>Whether the verified content is on this device.</summary>
    public async Task<bool> IsLocalAsync(BlobReference attachment) => await Store.SizeAsync(attachment.Sha256) == attachment.Size;

    /// <summary>Downloads an attachment, continuing an interrupted download.</summary>
    public async Task DownloadAsync(BlobReference attachment, CancellationToken cancellationToken = default)
    {
        await _transfer!.DownloadAsync(Store, attachment.Sha256, attachment.Size, cancellationToken);
        _uploaded[attachment.Sha256] = true; // it came from the server
    }

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

        foreach (var attachment in task.Attachments)
        {
            if (_uploaded.ContainsKey(attachment.Sha256) || await _store.SizeAsync(attachment.Sha256, cancellationToken) is null)
            {
                continue; // known to be on the server, or not on this device (it came from the server)
            }

            try
            {
                await _transfer.UploadAsync(_store, attachment.Sha256, cancellationToken);
                _uploaded[attachment.Sha256] = true;
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                return false;
            }
        }

        return true;
    }
}
