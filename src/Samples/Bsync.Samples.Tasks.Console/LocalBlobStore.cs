using System.Security.Cryptography;

namespace Bsync.Samples.Tasks.Console;

/// <summary>
/// The device's blob replica next to the SQLite file (task F1), content-addressed:
/// <list type="bullet">
/// <item><c>objects/ab/&lt;sha256&gt;</c>: verified content only. A file appears here by an atomic rename after its
/// hash was checked, so a partial or corrupted file is never opened as complete.</item>
/// <item><c>partial/&lt;sha256&gt;.part</c>: a download in progress, resumed after a crash or network loss.</item>
/// <item><c>uploaded/&lt;sha256&gt;</c>: a marker that the server holds the verified content for this user's tenant.</item>
/// <item><c>pins/&lt;task id&gt;</c>: tasks whose attachments are prefetched and never evicted.</item>
/// <item><c>bundles/&lt;bundle id&gt;.json</c>: the manifest of the bundle revision in use, replaced in one rename once
/// a newer revision is complete on the device (task F2).</item>
/// </list>
/// </summary>
public sealed class LocalBlobStore
{
    private readonly string _root;

    public LocalBlobStore(string root)
    {
        _root = root;
        foreach (var folder in new[] { "objects", "partial", "incoming", "uploaded", "pins", "bundles" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
        }
    }

    /// <summary>
    /// Copies <paramref name="content"/> into the store, flushed to disk, and returns its reference. The caller saves the
    /// document that names it only after this returns, so the bytes are durable before the save reports success.
    /// </summary>
    public async Task<BlobReference> ImportAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var incoming = Path.Combine(_root, "incoming", Guid.NewGuid().ToString("N"));
        string sha256;
        long size;
        try
        {
            await using (var file = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                size = file.Length;
            }

            Promote(incoming, sha256);
        }
        finally
        {
            File.Delete(incoming);
        }

        return new BlobReference(Guid.CreateVersion7().ToString(), sha256, size, contentType, fileName);
    }

    /// <summary>Whether the verified content is on this device.</summary>
    public bool Has(BlobReference blob) => new FileInfo(ObjectPath(blob.Sha256)) is { Exists: true } file && file.Length == blob.Size;

    /// <summary>Opens verified content, or returns <see langword="null"/> when it is not on this device (yet).</summary>
    public Stream? TryOpenRead(BlobReference blob)
    {
        if (!Has(blob))
        {
            return null;
        }

        var path = ObjectPath(blob.Sha256);
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow); // least-recently-used eviction
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    /// <summary>A source for uploading verified content.</summary>
    public IBlobSource Source(BlobReference blob) => new FileBlobSource(ObjectPath(blob.Sha256));

    /// <summary>Whether the server is known to hold the content.</summary>
    public bool IsUploaded(BlobReference blob) => File.Exists(Path.Combine(_root, "uploaded", blob.Sha256));

    /// <summary>Records that the server holds the content.</summary>
    public void MarkUploaded(BlobReference blob) => File.WriteAllBytes(Path.Combine(_root, "uploaded", blob.Sha256), []);

    /// <summary>Pins a task: its attachments are prefetched and never evicted.</summary>
    public void Pin(string taskId) => File.WriteAllBytes(Path.Combine(_root, "pins", Uri.EscapeDataString(taskId)), []);

    /// <summary>Unpins a task.</summary>
    public void Unpin(string taskId) => File.Delete(Path.Combine(_root, "pins", Uri.EscapeDataString(taskId)));

    /// <summary>The pinned tasks.</summary>
    public IReadOnlySet<string> PinnedTasks() =>
        Directory.EnumerateFiles(Path.Combine(_root, "pins")).Select(path => Uri.UnescapeDataString(Path.GetFileName(path))).ToHashSet(StringComparer.Ordinal);

    /// <summary>The manifest of the bundle revision in use, or <see langword="null"/>.</summary>
    public BundleManifest? ReadActiveBundle(string bundleId)
    {
        var path = BundlePath(bundleId);
        return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(path), TasksJson.Default.BundleManifest) : null;
    }

    /// <summary>
    /// Makes <paramref name="manifest"/> the revision in use, in one step: the file is written and flushed beside the old
    /// one and renamed over it, so a reader sees either the old or the new revision, never a mix.
    /// </summary>
    public void ActivateBundle(BundleManifest manifest)
    {
        var path = BundlePath(manifest.Id);
        var temporary = path + ".new";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            System.Text.Json.JsonSerializer.Serialize(file, manifest, TasksJson.Default.BundleManifest);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Stops using a bundle (it was deleted on the server).</summary>
    public void DeactivateBundle(string bundleId) => File.Delete(BundlePath(bundleId));

    /// <summary>The bundles in use.</summary>
    public IEnumerable<BundleManifest> ActiveBundles() =>
        Directory.EnumerateFiles(Path.Combine(_root, "bundles"), "*.json")
            .Select(path => System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(path), TasksJson.Default.BundleManifest)!);

    /// <summary>How many bytes of <paramref name="blob"/> a download has already received.</summary>
    public long PartialLength(BlobReference blob) => new FileInfo(PartialPath(blob)) is { Exists: true } file ? file.Length : 0;

    /// <summary>Where a download of <paramref name="blob"/> accumulates.</summary>
    public string PartialPath(BlobReference blob) => Path.Combine(_root, "partial", blob.Sha256 + ".part");

    /// <summary>Makes a downloaded and verified partial file the content.</summary>
    public void CompleteDownload(BlobReference blob)
    {
        var partial = PartialPath(blob);
        Promote(partial, blob.Sha256);
        File.Delete(partial);
        MarkUploaded(blob); // it came from the server
    }

    /// <summary>
    /// Removes least-recently-used content until the store holds at most <paramref name="maxBytes"/>, never removing
    /// content in <paramref name="keep"/> (named by pending documents or pinned). Returns the bytes removed.
    /// </summary>
    public long Evict(long maxBytes, IReadOnlySet<string> keep)
    {
        var files = Directory.EnumerateFiles(Path.Combine(_root, "objects"), "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToList();
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
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
        {
            File.Move(verified, target);
        }
    }

    private string ObjectPath(string sha256) => Path.Combine(_root, "objects", sha256[..2], sha256);

    private string BundlePath(string bundleId) => Path.Combine(_root, "bundles", Uri.EscapeDataString(bundleId) + ".json");
}
