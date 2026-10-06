using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Server-side blob storage (task F1): one object per SHA-256, plus partial uploads per uploader. Implementations may keep
/// objects in a file system, an S3-compatible bucket, or a database; the routes in <see cref="BlobEndpoints"/> do not
/// care.
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
    Task<long?> AppendAsync(string upload, long offset, Stream content, long limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that the partial upload hashes to <paramref name="sha256"/> and makes it the object (keeping an existing
    /// object instead, so there is never a second one). Returns <see langword="false"/> and discards the partial upload on a
    /// mismatch.
    /// </summary>
    Task<bool> CompleteAsync(string upload, string sha256, CancellationToken cancellationToken = default);
}

/// <summary>Blob storage in a directory: <c>objects/ab/&lt;sha256&gt;</c> and <c>uploads/&lt;upload&gt;.part</c>.</summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _objects;
    private readonly string _uploads;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public FileSystemBlobStore(string directory)
    {
        _objects = Path.Combine(directory, "objects");
        _uploads = Path.Combine(directory, "uploads");
        Directory.CreateDirectory(_objects);
        Directory.CreateDirectory(_uploads);
    }

    /// <summary>A file-name-safe upload key for a tenant's upload of a hash (one partial upload per tenant and content).</summary>
    public static string UploadKey(string tenant, string sha256) =>
        $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant)))[..32]}-{sha256}";

    public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(ObjectPath(sha256)));

    public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(File.Exists(ObjectPath(sha256))
            ? new FileStream(ObjectPath(sha256), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true)
            : null);

    public Task<long> GetReceivedAsync(string upload, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(UploadPath(upload));
        return Task.FromResult(file.Exists ? file.Length : 0L);
    }

    public async Task<long?> AppendAsync(string upload, long offset, Stream content, long limit, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(upload, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var file = new FileStream(UploadPath(upload), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
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
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (file.Length + read > limit)
                    {
                        throw new InvalidDataException("The upload is longer than announced.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                await file.FlushAsync(CancellationToken.None);
                file.Flush(flushToDisk: true);
            }

            return file.Length;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CompleteAsync(string upload, string sha256, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(upload, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var partial = UploadPath(upload);
            bool matches;
            await using (var file = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                matches = string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken)), sha256, StringComparison.Ordinal);
            }

            if (!matches)
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

    private string ObjectPath(string sha256) => Path.Combine(_objects, sha256[..2], sha256);

    private string UploadPath(string upload) => Path.Combine(_uploads, upload + ".part");
}
