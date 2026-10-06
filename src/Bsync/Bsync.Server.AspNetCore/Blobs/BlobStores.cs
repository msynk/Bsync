using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Bsync.Server.AspNetCore.Blobs;

/// <summary>
/// Server-side blob storage (task F1): one object per SHA-256, plus partial uploads per uploader. Implementations keep
/// objects in a file system (<see cref="FileSystemBlobStore"/>), an S3-compatible bucket (<c>Bsync.Server.Blobs.S3</c>)
/// or elsewhere; the routes of <see cref="SyncBlobEndpoints"/> do not care.
/// </summary>
public interface IBlobStore
{
    /// <summary>Whether a verified object with this hash exists.</summary>
    Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>Opens a verified object, or returns <see langword="null"/>.</summary>
    Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>How many bytes the partial upload <paramref name="upload"/> holds.</summary>
    Task<long> GetReceivedAsync(string upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends <paramref name="content"/> to the partial upload if it currently holds exactly <paramref name="offset"/>
    /// bytes, writing at most <paramref name="limit"/> bytes in total. Returns the bytes held afterwards, or
    /// <see langword="null"/> (and leaves the upload unchanged) when it holds a different number of bytes.
    /// </summary>
    /// <exception cref="InvalidDataException">The upload would exceed <paramref name="limit"/>.</exception>
    Task<long?> AppendAsync(string upload, long offset, Stream content, long limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that the partial upload hashes to <paramref name="sha256"/> and makes it the object (keeping an existing
    /// object instead, so there is never a second one). Returns <see langword="false"/> and discards the partial upload on a
    /// mismatch.
    /// </summary>
    Task<bool> CompleteAsync(string upload, string sha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// A short-lived URL to which a client sends the whole content of the upload <paramref name="upload"/> in one
    /// <c>PUT</c>, bypassing the application server, or <see langword="null"/> when the store has none (the default).
    /// Content stored that way is verified by <see cref="CompleteAsync"/> like a chunked upload, and
    /// <see cref="GetReceivedAsync"/> reports its full length once stored.
    /// </summary>
    Task<Uri?> PresignWriteAsync(string upload, long size, TimeSpan validity, CancellationToken cancellationToken = default) => Task.FromResult<Uri?>(null);

    /// <summary>A short-lived URL serving the object directly, or <see langword="null"/> when the store has none.</summary>
    Task<Uri?> PresignReadAsync(string sha256, TimeSpan validity, CancellationToken cancellationToken = default) => Task.FromResult<Uri?>(null);

    /// <summary>Every object, with when it was stored (for <see cref="SyncBlobCollector"/>).</summary>
    IAsyncEnumerable<(string Sha256, DateTimeOffset Stored)> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes an object.</summary>
    Task DeleteAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>Deletes partial uploads not appended to since <paramref name="before"/>. Returns how many.</summary>
    Task<int> PurgePartialsAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}

/// <summary>Blob storage in a directory: <c>objects/ab/&lt;sha256&gt;</c> and <c>uploads/&lt;upload&gt;.part</c>.</summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _objects;
    private readonly string _uploads;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>Creates the store in <paramref name="directory"/> (created if needed).</summary>
    public FileSystemBlobStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _objects = Path.Combine(directory, "objects");
        _uploads = Path.Combine(directory, "uploads");
        Directory.CreateDirectory(_objects);
        Directory.CreateDirectory(_uploads);
    }

    /// <summary>A file-name-safe key for one caller scope's upload of a hash (one partial upload per scope and content).</summary>
    public static string UploadKey(string scope, string sha256) =>
        $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32]}-{sha256}";

    /// <summary>The file of a partial upload (for stores that keep only partial uploads here, such as the S3 store).</summary>
    public string UploadFile(string upload) => Path.Combine(_uploads, upload + ".part");

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(ObjectPath(sha256)));

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(File.Exists(ObjectPath(sha256))
            ? new FileStream(ObjectPath(sha256), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true)
            : null);

    /// <inheritdoc />
    public Task<long> GetReceivedAsync(string upload, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FileInfo(UploadFile(upload)) is { Exists: true } file ? file.Length : 0L);

    /// <inheritdoc />
    public async Task<long?> AppendAsync(string upload, long offset, Stream content, long limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var gate = _locks.GetOrAdd(upload, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var file = new FileStream(UploadFile(upload), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            if (file.Length != offset)
            {
                return null;
            }

            file.Position = offset;
            var buffer = new byte[81920];
            int read;
            try
            {
                // Bytes are kept as they arrive: a request cut off mid-body still leaves a correct prefix to resume from.
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (file.Length + read > limit)
                    {
                        throw new InvalidDataException("The upload is longer than allowed.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            return file.Length;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(string upload, string sha256, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(upload, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var partial = UploadFile(upload);
            if (!await VerifyAsync(partial, sha256, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(partial);
                return false;
            }

            var target = ObjectPath(sha256);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                File.Delete(partial); // deduplicated: the content is already stored once
            }
            else
            {
                File.Move(partial, target);
            }

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(string Sha256, DateTimeOffset Stored)> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var path in Directory.EnumerateFiles(_objects, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (Path.GetFileName(path), new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero));
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string sha256, CancellationToken cancellationToken = default)
    {
        File.Delete(ObjectPath(sha256));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> PurgePartialsAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var file in new DirectoryInfo(_uploads).EnumerateFiles("*.part"))
        {
            if (file.LastWriteTimeUtc < before.UtcDateTime)
            {
                file.Delete();
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    /// <summary>Whether the file at <paramref name="path"/> hashes to <paramref name="sha256"/>.</summary>
    public static async Task<bool> VerifyAsync(string path, string sha256, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)), sha256, StringComparison.Ordinal);
    }

    private string ObjectPath(string sha256) => Path.Combine(_objects, sha256[..2], sha256);
}

/// <summary>
/// The application's rules for blobs (task F1): who holds which content, and who may read it. Typically backed by the
/// application's tables, for example "a caller may read a blob that a document it can read references".
/// </summary>
public interface IBlobAccess
{
    /// <summary>Whether the caller's scope has uploaded (and the server verified) <paramref name="sha256"/>.</summary>
    Task<bool> HoldsAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default);

    /// <summary>Records that the caller's scope uploaded verified content. Called once per finished upload; may repeat.</summary>
    Task RecordAsync(SyncCallContext caller, string sha256, long size, CancellationToken cancellationToken = default);

    /// <summary>Whether the caller may read <paramref name="sha256"/>.</summary>
    Task<bool> CanReadAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default);
}

/// <summary>
/// Removes stored content nothing references any more (task F1): objects older than a grace period that are not in the
/// referenced set, and partial uploads abandoned for longer than that. Run it on a schedule (for example next to
/// <c>AddSyncRetention</c>); the grace period protects uploads whose documents have not been written yet.
/// </summary>
public static class SyncBlobCollector
{
    /// <summary>Collects unreferenced objects stored before <c>now - minimumAge</c>. Returns objects and partial uploads removed.</summary>
    public static async Task<(int Objects, int Partials)> CollectAsync(
        IBlobStore store,
        Func<CancellationToken, Task<IReadOnlySet<string>>> referenced,
        TimeSpan minimumAge,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(referenced);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAge, TimeSpan.Zero);
        var cutoff = (time ?? TimeProvider.System).GetUtcNow() - minimumAge;
        var keep = await referenced(cancellationToken).ConfigureAwait(false);
        var candidates = new List<string>();
        await foreach (var (sha256, stored) in store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (stored < cutoff && !keep.Contains(sha256))
            {
                candidates.Add(sha256);
            }
        }

        // Re-read the references just before deleting, so content referenced meanwhile is kept.
        keep = await referenced(cancellationToken).ConfigureAwait(false);
        var objects = 0;
        foreach (var sha256 in candidates.Where(c => !keep.Contains(c)))
        {
            await store.DeleteAsync(sha256, cancellationToken).ConfigureAwait(false);
            objects++;
        }

        return (objects, await store.PurgePartialsAsync(cutoff, cancellationToken).ConfigureAwait(false));
    }
}
