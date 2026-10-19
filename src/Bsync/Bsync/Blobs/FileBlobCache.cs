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
public sealed class FileBlobCache : IBlobCacheReplacement, IBlobCacheInventory, IBlobCachePagedInventory
{
    /// <inheritdoc />
    public bool SupportsVerifiedReplacement => true;
    private readonly string _objects;
    private readonly string _partial;
    private readonly string _retired;
    private readonly SemaphoreSlim _retiredCollection = new(1, 1);
    private string? _retiredContinuation;

    /// <summary>Creates the cache in <paramref name="directory"/> (created if needed).</summary>
    public FileBlobCache(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        _objects = Path.Combine(directory, "objects");
        _partial = Path.Combine(directory, "partial");
        _retired = Path.Combine(directory, "retired");
        System.IO.Directory.CreateDirectory(_objects);
        System.IO.Directory.CreateDirectory(_partial);
        System.IO.Directory.CreateDirectory(_retired);
    }

    /// <summary>The cache's directory.</summary>
    public string Directory { get; }

    /// <inheritdoc />
    public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(System.IO.Directory.EnumerateFiles(_objects, "*", SearchOption.AllDirectories)
            .Concat(System.IO.Directory.EnumerateFiles(_partial)).Concat(System.IO.Directory.EnumerateFiles(_retired)).Sum(path => new FileInfo(path).Length));
    }

    /// <summary>
    /// Reclaims at most limit retired replacement files only after exclusive physical open proves all old readers
    /// released them. Suspended readers remain protected; process death releases handles. Unknown filenames remain.
    /// Returned bytes were removed. Retired bytes remain included in physical inventory until successfully reclaimed.
    /// </summary>
    public async Task<long> CollectRetiredAsync(int limit = 128, CancellationToken cancellationToken = default)
    {
        await _retiredCollection.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var page = await CollectRetiredPageAsync(_retiredContinuation, limit, cancellationToken).ConfigureAwait(false);
            _retiredContinuation = page.Continuation;
            return page.RemovedBytes;
        }
        finally { _retiredCollection.Release(); }
    }

    /// <summary>
    /// Probes at most limit files in ordinal filename order, including unknown or busy files in progress.
    /// Persist Continuation across cache instances; restart at null after the end to revisit busy readers and new files.
    /// Directory enumeration is linear but retains at most limit names and checks cancellation per entry.
    /// The caller must hold account lifetime ownership. Unknown filenames are never deleted.
    /// </summary>
    public Task<BlobRetiredCollectionResult> CollectRetiredPageAsync(string? afterFileName = null, int limit = 128,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        if (afterFileName is not null && (afterFileName.Length == 0 || Path.GetFileName(afterFileName) != afterFileName ||
            afterFileName.IndexOfAny(['/', '\\']) >= 0)) throw new ArgumentException("Expected a filename continuation.", nameof(afterFileName));
        cancellationToken.ThrowIfCancellationRequested();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in System.IO.Directory.EnumerateFiles(_retired))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (afterFileName is not null && StringComparer.Ordinal.Compare(name, afterFileName) <= 0) continue;
            names.Add(name);
            if (names.Count > limit) names.Remove(names.Max!);
        }
        long removed = 0;
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name.Length != 101 || name[64] != '-' || !name.EndsWith(".old", StringComparison.Ordinal) ||
                !name[..64].All(char.IsAsciiHexDigitLower) || !Guid.TryParseExact(name.AsSpan(65, 32), "N", out _)) continue;
            removed = checked(removed + TryReclaim(Path.Combine(_retired, name)));
        }
        return Task.FromResult(new BlobRetiredCollectionResult(names.Count, removed, names.Count == limit ? names.Max : null));
    }

    private static long TryReclaim(string path)
    {
        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose);
            return exclusive.Length;
        }
        catch (FileNotFoundException) { return 0; }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11) { return 0; }
    }

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
            cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>Scans directory entries with at most limit retained identities; time is linear in directory size, memory is bounded.</summary>
    public Task<IReadOnlyList<string>> ListCompletedPageAsync(string? afterHash = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        var page = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in System.IO.Directory.EnumerateFiles(_objects, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Path.GetFileName(path);
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigitLower) || (afterHash is not null && string.CompareOrdinal(hash, afterHash) <= 0)) continue;
            page.Add(hash); if (page.Count > limit) page.Remove(page.Max!);
        }
        return Task.FromResult<IReadOnlyList<string>>(page.ToArray());
    }

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
        // The staging file is already flushed and hash-verified. Replace in one filesystem rename so a corrupt
        // same-name object can be repaired without a missing-content interval. Existing readers allow delete sharing.
        if (File.Exists(target)) Replace();
        else
        {
            try { File.Move(verified, target); }
            catch (IOException) when (File.Exists(target))
            { Replace(); }
        }
        void Replace()
        {
            // Preserve a physical inventory name for the old inode instead of unlinking reader-held bytes.
            var backup = Path.Combine(_retired, sha256 + "-" + Guid.NewGuid().ToString("N") + ".old");
            File.Replace(verified, target, backup);
            _ = TryReclaim(backup);
        }
    }

    private string ObjectPath(string sha256) => Path.Combine(_objects, Check(sha256)[..2], sha256);

    private string PartialPath(string sha256) => Path.Combine(_partial, Check(sha256) + ".part");

    private static string Check(string sha256) =>
        sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigitLower) ? sha256 : throw new ArgumentException("Not a lowercase SHA-256.", nameof(sha256));
}
