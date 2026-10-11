using Bsync.Blobs;

namespace Bsync.Samples.Tasks.Console;

/// <summary>
/// The device's attachment state next to the SQLite file (task F1): the verified content itself is a
/// <see cref="FileBlobCache"/> (<c>objects/</c>, <c>partial/</c>); this class adds what the app decides on top of it:
/// <list type="bullet">
/// <item><c>uploaded/&lt;sha256&gt;</c>: a marker that the server holds the verified content for this user's tenant.</item>
/// <item><c>pins/&lt;task id&gt;</c>: tasks whose attachments are prefetched and never evicted.</item>
/// <item><c>bundles/&lt;bundle id&gt;.json</c>: the manifest of the bundle revision in use, replaced in one rename once
/// a newer revision is complete on the device (task F2).</item>
/// </list>
/// The sample is synchronous where the file cache completes synchronously anyway.
/// </summary>
public sealed class LocalBlobStore
{
    private readonly string _root;

    public LocalBlobStore(string root)
    {
        _root = root;
        Content = new FileBlobCache(root);
        foreach (var folder in new[] { "uploaded", "pins", "bundles" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
        }
    }

    /// <summary>The verified content.</summary>
    public FileBlobCache Content { get; }

    /// <summary>
    /// Copies <paramref name="content"/> into the store, flushed to disk, and returns its reference. The caller saves the
    /// document that names it only after this returns, so the bytes are durable before the save reports success.
    /// </summary>
    public async Task<BlobReference> ImportAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var (sha256, size) = await Content.ImportAsync(content, cancellationToken);
        return new BlobReference(Guid.CreateVersion7().ToString(), sha256, size, contentType, fileName);
    }

    /// <summary>Whether the verified content is on this device.</summary>
    public bool Has(BlobReference blob) => Content.SizeAsync(blob.Sha256).GetAwaiter().GetResult() == blob.Size;

    /// <summary>Opens verified content, or returns <see langword="null"/> when it is not on this device (yet).</summary>
    public Stream? TryOpenRead(BlobReference blob) => Has(blob) ? Content.OpenReadAsync(blob.Sha256).GetAwaiter().GetResult() : null;

    /// <summary>Whether the server is known to hold the content.</summary>
    public bool IsUploaded(BlobReference blob) => File.Exists(Path.Combine(_root, "uploaded", blob.Sha256));

    /// <summary>Records that the server holds the content.</summary>
    public void MarkUploaded(BlobReference blob) => File.WriteAllBytes(Path.Combine(_root, "uploaded", blob.Sha256), []);

    /// <summary>How many bytes of an interrupted download of <paramref name="blob"/> are kept.</summary>
    public long PartialLength(BlobReference blob) => Content.PartialLengthAsync(blob.Sha256).GetAwaiter().GetResult();

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

    /// <summary>
    /// Removes least-recently-used content until the store holds at most <paramref name="maxBytes"/>, never removing
    /// content in <paramref name="keep"/> (named by pending documents or pinned). Returns the bytes removed.
    /// </summary>
    public long Evict(long maxBytes, IReadOnlySet<string> keep) => Content.Evict(maxBytes, keep);

    private string BundlePath(string bundleId) => Path.Combine(_root, "bundles", Uri.EscapeDataString(bundleId) + ".json");
}
