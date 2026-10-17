using System.Security.Cryptography;
using System.Text;
using Bsync.Blobs;
using Bsync.Storage;

namespace Bsync.Samples.Shared;

/// <summary>Host-specific storage acquisition; the same controller and UI run on browser and native storage.</summary>
public interface IContentLibraryFactory
{
    Task<ContentLibrarySession> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>A tiny content library with two publisher-authored revisions bundled into the sample application.</summary>
public sealed class ContentLibrarySession : IAsyncDisposable
{
    private readonly ManagedBlobCache _cache;
    private readonly Func<ValueTask> _close;
    private readonly HttpClient _http = new(new NoNetwork()) { BaseAddress = new("https://bundled-content.invalid/") };
    public OfflinePackageManager Packages { get; }

    public ContentLibrarySession(IOrchestrationStore metadata, ManagedBlobCache cache, IBlobCacheLock locks, Func<ValueTask> close)
    {
        _cache = cache; _close = close;
        Packages = new(metadata, cache, new(metadata, cache, new(_http)), locks, (_, _) => Task.FromResult(true));
    }

    public async Task PrepareAsync(int revision, bool interrupt, CancellationToken cancellationToken = default)
    {
        if (revision is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(revision));
        var content = new[] { Encoding.UTF8.GetBytes("Field guide: prepared content opens without a server."),
            Encoding.UTF8.GetBytes(revision == 1 ? "Revision 1: carry a local copy before leaving." : "Revision 2: a verified update replaces the whole guide.") };
        var items = content.Select((bytes, index) => new OfflinePackageItem("chapter-" + index,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, true, [])).ToArray();
        await Packages.StageAsync(new(1, "field-guide", revision.ToString(System.Globalization.CultureInfo.InvariantCulture), null, items), cancellationToken);
        await _cache.ImportAsync(new MemoryStream(content[0], writable: false), cancellationToken);
        if (interrupt) return; // Deliberately persisted preparation with an unavailable second chapter.
        await _cache.ImportAsync(new MemoryStream(content[1], writable: false), cancellationToken);
        if (!await Packages.TryActivateAsync("field-guide", revision.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken))
            throw new IOException("The candidate is incomplete or a newer revision was staged.");
    }

    public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var snapshot = await Packages.OpenActiveAsync("field-guide", cancellationToken);
        var chapters = new List<string>();
        foreach (var id in new[] { "chapter-0", "chapter-1" })
        {
            await using var stream = await snapshot.OpenAsync(id, cancellationToken) ?? throw new FileNotFoundException("Prepared chapter is missing.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            chapters.Add(await reader.ReadToEndAsync(cancellationToken));
        }
        return string.Join("\n", chapters);
    }

    public async ValueTask DisposeAsync() { _http.Dispose(); await _close(); }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This sample's publisher content is bundled; opening never contacts a server.");
    }
}
