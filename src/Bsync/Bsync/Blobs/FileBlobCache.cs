using System.Security.Cryptography;

namespace Bsync.Blobs;

/// <summary>
/// An <see cref="IBlobCache"/> in a directory (native hosts, next to the SQLite replica):
/// <list type="bullet">
/// <item><c>objects/ab/&lt;sha256&gt;</c>: verified content. A file appears here only by an atomic rename after its hash
/// was checked.</item>
/// <item><c>partial/&lt;sha256&gt;.part</c>: a transfer in progress, flushed after every append.</item>
/// </list>
/// </summary>
public sealed class FileBlobCache : IBlobCache
{
    private readonly string _objects;
    private readonly string _partial;

    /// <summary>Creates the cache in <paramref name="directory"/> (created if needed).</summary>
    public FileBlobCache(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        _objects = Path.Combine(directory, "objects");
        _partial = Path.Combine(directory, "partial");
        System.IO.Directory.CreateDirectory(_objects);
        System.IO.Directory.CreateDirectory(_partial);
    }

    /// <summary>The cache's directory.</summary>
    public string Directory { get; }

    /// <inheritdoc />
    public Task<long?> SizeAsync(string sha256, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FileInfo(ObjectPath(sha256)) is { Exists: true } file ? file.Length : (long?)null);

    /// <inheritdoc />
    public Task<long> PartialLengthAsync(string sha256, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FileInfo(PartialPath(sha256)) is { Exists: true } file ? file.Length : 0L);

    /// <inheritdoc />
    public async Task AppendPartialAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(PartialPath(sha256), FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public async Task<bool> CompletePartialAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var partial = PartialPath(sha256);
        if (!File.Exists(partial))
        {
            return false;
        }

        bool matches;
        await using (var file = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            matches = string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)), sha256, StringComparison.Ordinal);
        }

        if (matches)
        {
            Promote(partial, sha256);
        }

        File.Delete(partial);
        return matches;
    }

    /// <inheritdoc />
    public Task DiscardPartialAsync(string sha256, CancellationToken cancellationToken = default)
    {
        File.Delete(PartialPath(sha256));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var path = ObjectPath(sha256);
        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        File.SetLastAccessTimeUtc(path, DateTime.UtcNow); // least-recently-used eviction
        return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true));
    }

    /// <inheritdoc />
    public async Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var incoming = Path.Combine(_partial, $"import-{Guid.NewGuid():N}");
        try
        {
            string sha256;
            long size;
            await using (var file = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                size = file.Length;
            }

            Promote(incoming, sha256);
            return (sha256, size);
        }
        finally
        {
            File.Delete(incoming);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string sha256, CancellationToken cancellationToken = default)
    {
        File.Delete(ObjectPath(sha256));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. System.IO.Directory.EnumerateFiles(_objects, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>()]);

    /// <summary>
    /// Removes least-recently-used content until at most <paramref name="maxBytes"/> remain, never content in
    /// <paramref name="keep"/> (named by pending documents or pinned). Returns the bytes removed.
    /// </summary>
    public long Evict(long maxBytes, IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var files = System.IO.Directory.EnumerateFiles(_objects, "*", SearchOption.AllDirectories).Select(path => new FileInfo(path)).ToList();
        var total = files.Sum(f => f.Length);
        var removed = 0L;
        foreach (var file in files.Where(f => !keep.Contains(f.Name)).OrderBy(f => f.LastAccessTimeUtc))
        {
            if (total - removed <= maxBytes)
            {
                break;
            }

            removed += file.Length;
            file.Delete();
        }

        return removed;
    }

    private void Promote(string verified, string sha256)
    {
        var target = ObjectPath(sha256);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
        {
            File.Move(verified, target);
        }
    }

    private string ObjectPath(string sha256) => Path.Combine(_objects, Check(sha256)[..2], sha256);

    private string PartialPath(string sha256) => Path.Combine(_partial, Check(sha256) + ".part");

    private static string Check(string sha256) =>
        sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigitLower) ? sha256 : throw new ArgumentException("Not a lowercase SHA-256.", nameof(sha256));
}
