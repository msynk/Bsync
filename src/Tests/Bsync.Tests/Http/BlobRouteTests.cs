using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Server.AspNetCore.Blobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>
/// Task F1: the blob routes (<see cref="SyncBlobEndpoints"/>), <see cref="HttpBlobTransfer"/>, <see cref="FileBlobCache"/>,
/// <see cref="FileSystemBlobStore"/>, <see cref="SyncBlobCollector"/> and the blob metrics (task H). The tests of this
/// class run one at a time, so the meters see only their transfers.
/// </summary>
public sealed class BlobRouteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsync-blobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Holds per scope; a scope may read what it holds plus what <see cref="Grant"/> allows.</summary>
    private sealed class Access : IBlobAccess
    {
        private readonly ConcurrentDictionary<(string Scope, string Sha256), long> _held = new();
        private readonly ConcurrentDictionary<(string Scope, string Sha256), bool> _granted = new();

        public int Recorded;

        public void Grant(string scope, string sha256) => _granted[(scope, sha256)] = true;

        public Task<bool> HoldsAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default) =>
            Task.FromResult(_held.ContainsKey((caller.Scope, sha256)));

        public Task RecordAsync(SyncCallContext caller, string sha256, long size, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Recorded);
            _held[(caller.Scope, sha256)] = size;
            return Task.CompletedTask;
        }

        public Task<bool> CanReadAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default) =>
            Task.FromResult(_held.ContainsKey((caller.Scope, sha256)) || _granted.ContainsKey((caller.Scope, sha256)));
    }

    /// <summary>
    /// A store offering presigned uploads, as an object store does: the "presigned URL" is a route of the test server that
    /// writes the whole body into the partial upload without authentication.
    /// </summary>
    private sealed class PresigningStore(FileSystemBlobStore inner) : IBlobStore
    {
        public Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default) => inner.ExistsAsync(sha256, cancellationToken);

        public Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default) => inner.OpenReadAsync(sha256, cancellationToken);

        public Task<long> GetReceivedAsync(string upload, CancellationToken cancellationToken = default) => inner.GetReceivedAsync(upload, cancellationToken);

        public Task<long?> AppendAsync(string upload, long offset, Stream content, long limit, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(upload, offset, content, limit, cancellationToken);

        public Task<bool> CompleteAsync(string upload, string sha256, CancellationToken cancellationToken = default) => inner.CompleteAsync(upload, sha256, cancellationToken);

        public Task<Uri?> PresignWriteAsync(string upload, long size, TimeSpan validity, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(new Uri($"http://localhost/direct/{upload}"));

        public IAsyncEnumerable<(string Sha256, DateTimeOffset Stored)> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);

        public Task DeleteAsync(string sha256, CancellationToken cancellationToken = default) => inner.DeleteAsync(sha256, cancellationToken);

        public Task<int> PurgePartialsAsync(DateTimeOffset before, CancellationToken cancellationToken = default) => inner.PurgePartialsAsync(before, cancellationToken);
    }

    private sealed class Host(WebApplication app, FileSystemBlobStore files, Access access) : IAsyncDisposable
    {
        public FileSystemBlobStore Files => files;

        public Access Access => access;

        public int DirectPuts;

        public HttpClient Client(string tenant)
        {
            var client = app.GetTestServer().CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "user-" + tenant);
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenant);
            return client;
        }

        public HttpClient Anonymous() => app.GetTestServer().CreateClient();

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private async Task<Host> StartAsync(bool presign = false, long maxSize = 200L * 1024 * 1024)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var files = new FileSystemBlobStore(Path.Combine(_root, "server"));
        var access = new Access();
        Host? host = null;
        IBlobStore store = presign ? new PresigningStore(files) : files;
        app.MapSyncBlobs(
                store,
                access,
                new SyncEndpointOptions { SupportedSchemas = new HashSet<string>(["blobs-v1"]), ResolveScope = http => http.User.FindFirst("tenant")?.Value },
                new SyncBlobEndpointOptions { MaxSize = maxSize })
            .RequireAuthorization();
        app.MapPut("/direct/{upload}", (RequestDelegate)(async http =>
        {
            Interlocked.Increment(ref host!.DirectPuts);
            var upload = (string)http.Request.RouteValues["upload"]!;
            File.Delete(files.UploadFile(upload)); // a PUT replaces the object
            await files.AppendAsync(upload, 0, http.Request.Body, long.MaxValue, http.RequestAborted);
            http.Response.StatusCode = StatusCodes.Status200OK;
        }));
        await app.StartAsync();
        host = new Host(app, files, access);
        return host;
    }

    private static byte[] Content(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private async Task<(FileBlobCache Cache, string Sha256)> CachedAsync(string device, byte[] content)
    {
        var cache = new FileBlobCache(Path.Combine(_root, device));
        var (sha256, _) = await cache.ImportAsync(new MemoryStream(content));
        return (cache, sha256);
    }

    private static int ObjectCount(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "server", "objects"), "*", SearchOption.AllDirectories).Count();

    [Fact(DisplayName = "F1 I01: content uploads in chunks, downloads to another device verified, and is stored once even when two scopes upload it")]
    public async Task RoundTrip()
    {
        await using var host = await StartAsync();
        var content = Content(10_000, 1);
        var (alice, sha256) = await CachedAsync("alice", content);
        var options = new HttpBlobTransferOptions { ChunkSize = 1024 };

        var sent = await new HttpBlobTransfer(host.Client("team-1"), options).UploadAsync(alice, sha256);
        var again = await new HttpBlobTransfer(host.Client("team-1"), options).UploadAsync(alice, sha256);
        var other = await new HttpBlobTransfer(host.Client("team-2"), options).UploadAsync(alice, sha256);

        Assert.Equal((10_000L, 0L), (sent, again)); // the scope holds it: nothing to send
        Assert.Equal(10_000L, other); // another scope proves it has the content by sending it
        Assert.Equal(1, ObjectCount(_root));

        var bob = new FileBlobCache(Path.Combine(_root, "bob"));
        Assert.Equal(10_000L, await new HttpBlobTransfer(host.Client("team-1"), options).DownloadAsync(bob, sha256, content.Length));
        await using var opened = await bob.OpenReadAsync(sha256);
        Assert.Equal(sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(opened!)));
    }

    [Fact(DisplayName = "F1: a scope that may not read content gets 404, whether or not the content exists")]
    public async Task ReadsAreScoped()
    {
        await using var host = await StartAsync();
        var (alice, sha256) = await CachedAsync("alice", Content(100, 2));
        await new HttpBlobTransfer(host.Client("team-1")).UploadAsync(alice, sha256);
        var missing = new string('0', 64);

        Assert.Equal(HttpStatusCode.OK, (await host.Client("team-1").GetAsync($"sync/blobs/{sha256}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client("team-2").GetAsync($"sync/blobs/{sha256}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client("team-2").GetAsync($"sync/blobs/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Anonymous().GetAsync($"sync/blobs/{sha256}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client("team-1").GetAsync($"sync/blobs/{sha256.ToUpperInvariant()}")).StatusCode);

        host.Access.Grant("team-2", sha256); // for example, a document team-2 can read references it
        Assert.Equal(HttpStatusCode.OK, (await host.Client("team-2").GetAsync($"sync/blobs/{sha256}")).StatusCode);
    }

    [Fact(DisplayName = "F1: an append at the wrong offset is refused with the server's offset; the upload continues from there")]
    public async Task OffsetMismatchReportsTheRealOffset()
    {
        await using var host = await StartAsync();
        var content = Content(3000, 3);
        var sha256 = Sha(content);
        var http = host.Client("team-1");
        await http.PostAsync($"sync/blobs/{sha256}/uploads?size=3000", null);
        (await http.PutAsync($"sync/blobs/{sha256}/uploads/0", new ByteArrayContent(content[..1000]))).EnsureSuccessStatusCode();

        var stale = await http.PutAsync($"sync/blobs/{sha256}/uploads/0", new ByteArrayContent(content[..1000]));
        var (alice, _) = await CachedAsync("alice", content);
        var sent = await new HttpBlobTransfer(http).UploadAsync(alice, sha256);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("\"received\":1000", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(2000L, sent);
        Assert.Equal(1, ObjectCount(_root));
    }

    [Fact(DisplayName = "F1 I01: bytes that do not match the hash are discarded with 422 and never become content")]
    public async Task HashMismatchIsDiscarded()
    {
        await using var host = await StartAsync();
        var claimed = Sha(Content(500, 4));
        var http = host.Client("team-1");

        var error = await Assert.ThrowsAsync<BlobCorruptedException>(() => new HttpBlobTransfer(http).UploadAsync(claimed, new BytesSource(Content(500, 5))));
        var status = await http.PostAsync($"sync/blobs/{claimed}/uploads?size=500", null);

        Assert.Contains(claimed, error.Message, StringComparison.Ordinal);
        Assert.Contains("\"received\":0", await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, ObjectCount(_root));
        Assert.Equal(0, host.Access.Recorded);
    }

    [Fact(DisplayName = "F1: content above the size limit is refused before any byte is stored")]
    public async Task TooLargeIsRefused()
    {
        await using var host = await StartAsync(maxSize: 1000);
        var http = host.Client("team-1");
        var (alice, sha256) = await CachedAsync("alice", Content(1001, 6));

        var start = await http.PostAsync($"sync/blobs/{sha256}/uploads?size=1001", null);
        var append = await http.PutAsync($"sync/blobs/{sha256}/uploads/0", new ByteArrayContent(Content(1001, 6)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, start.StatusCode);
        Assert.Contains(BlobCodes.TooLarge, await start.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, append.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpBlobTransfer(http).UploadAsync(alice, sha256));
    }

    [Fact(DisplayName = "F1: an interrupted download continues from the bytes the device kept, by a range request")]
    public async Task DownloadResumes()
    {
        await using var host = await StartAsync();
        var content = Content(8000, 7);
        var (alice, sha256) = await CachedAsync("alice", content);
        await new HttpBlobTransfer(host.Client("team-1")).UploadAsync(alice, sha256);
        var bob = new FileBlobCache(Path.Combine(_root, "bob"));
        await bob.AppendPartialAsync(sha256, content.AsMemory(0, 3000)); // what arrived before the connection dropped

        var received = await new HttpBlobTransfer(host.Client("team-1"), new HttpBlobTransferOptions { ChunkSize = 1024 }).DownloadAsync(bob, sha256, content.Length);

        Assert.Equal(5000L, received);
        Assert.Equal(content.Length, await bob.SizeAsync(sha256));
        Assert.Equal(0L, await bob.PartialLengthAsync(sha256));
    }

    [Fact(DisplayName = "F1 I01: a download whose bytes do not match the hash is discarded, and the next attempt starts over")]
    public async Task CorruptDownloadIsDiscarded()
    {
        await using var host = await StartAsync();
        var content = Content(4000, 8);
        var (alice, sha256) = await CachedAsync("alice", content);
        await new HttpBlobTransfer(host.Client("team-1")).UploadAsync(alice, sha256);
        var bob = new FileBlobCache(Path.Combine(_root, "bob"));
        await bob.AppendPartialAsync(sha256, new byte[1000]); // kept from a bad earlier transfer
        var transfer = new HttpBlobTransfer(host.Client("team-1"));

        await Assert.ThrowsAsync<BlobCorruptedException>(() => transfer.DownloadAsync(bob, sha256, content.Length));
        var received = await transfer.DownloadAsync(bob, sha256, content.Length);

        Assert.Equal(4000L, received);
        Assert.Equal(content.Length, await bob.SizeAsync(sha256));
    }

    [Fact(DisplayName = "F1: with a store that presigns uploads, content goes in one request to the URL and is verified at finish")]
    public async Task PresignedUploads()
    {
        await using var host = await StartAsync(presign: true);
        var (alice, sha256) = await CachedAsync("alice", Content(5000, 9));
        var direct = host.Anonymous();

        var sent = await new HttpBlobTransfer(host.Client("team-1"), new HttpBlobTransferOptions { ChunkSize = 1024, DirectClient = direct }).UploadAsync(alice, sha256);
        var chunked = await new HttpBlobTransfer(host.Client("team-2"), new HttpBlobTransferOptions { ChunkSize = 1024, DirectUploads = false }).UploadAsync(alice, sha256);
        var forged = Sha(Content(10, 10));
        var refused = await Assert.ThrowsAsync<BlobCorruptedException>(() =>
            new HttpBlobTransfer(host.Client("team-3"), new HttpBlobTransferOptions { DirectClient = direct }).UploadAsync(forged, new BytesSource(Content(10, 11))));

        Assert.Equal((5000L, 5000L), (sent, chunked));
        Assert.Equal(2, host.DirectPuts); // alice's, and the forged one; team-2 chose chunks
        Assert.Equal(1, ObjectCount(_root));
        Assert.Equal(2, host.Access.Recorded);
        Assert.NotNull(refused);
    }

    [Fact(DisplayName = "F1: the collector removes unreferenced content older than the grace period and abandoned partial uploads")]
    public async Task CollectorRemovesUnreferencedContent()
    {
        await using var host = await StartAsync();
        var (alice, kept) = await CachedAsync("alice", Content(100, 12));
        var dropped = (await alice.ImportAsync(new MemoryStream(Content(100, 13)))).Sha256;
        var transfer = new HttpBlobTransfer(host.Client("team-1"));
        await transfer.UploadAsync(alice, kept);
        await transfer.UploadAsync(alice, dropped);
        await host.Client("team-1").PutAsync($"sync/blobs/{Sha(Content(50, 14))}/uploads/0", new ByteArrayContent(Content(20, 14))); // abandoned
        var later = new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(2));
        var calls = 0;
        Task<IReadOnlySet<string>> Referenced(CancellationToken _)
        {
            calls++;
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>([kept]));
        }

        var early = await SyncBlobCollector.CollectAsync(host.Files, Referenced, TimeSpan.FromHours(3), later);
        var late = await SyncBlobCollector.CollectAsync(host.Files, Referenced, TimeSpan.FromHours(1), later);

        Assert.Equal((0, 0), early);
        Assert.Equal((1, 1), late);
        Assert.Equal(4, calls); // read twice per run: content referenced meanwhile is kept
        Assert.True(await host.Files.ExistsAsync(kept));
        Assert.False(await host.Files.ExistsAsync(dropped));
    }

    [Fact(DisplayName = "H F1: blob bytes and transfers are counted on the client and server meters, by direction, endpoint and result")]
    public async Task TransfersAreMeasured()
    {
        var measurements = new ConcurrentQueue<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name.Contains(".blob.", StringComparison.Ordinal) && instrument.Meter.Name is "Bsync" or SyncEndpoints.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            measurements.Enqueue((instrument.Name, value, copy));
        });
        listener.Start();
        await using var host = await StartAsync();
        var content = Content(3000, 15);
        var (alice, sha256) = await CachedAsync("alice", content);
        var transfer = new HttpBlobTransfer(host.Client("team-1"), new HttpBlobTransferOptions { ChunkSize = 1024 });

        await transfer.UploadAsync(alice, sha256);
        await transfer.DownloadAsync(new FileBlobCache(Path.Combine(_root, "bob")), sha256, content.Length);
        await Assert.ThrowsAsync<BlobCorruptedException>(() => transfer.UploadAsync(Sha(Content(10, 16)), new BytesSource(Content(10, 17))));

        long Sum(string instrument, params (string Key, string Value)[] tags) =>
            measurements.Where(m => m.Instrument == instrument && tags.All(t => Equals(m.Tags.GetValueOrDefault(t.Key), t.Value))).Sum(m => m.Value);

        Assert.Equal(3010, Sum("bsync.blob.bytes", ("bsync.direction", "upload")));
        Assert.Equal(3000, Sum("bsync.blob.bytes", ("bsync.direction", "download")));
        Assert.Equal(1, Sum("bsync.blob.transfers", ("bsync.direction", "upload"), ("bsync.result", "complete")));
        Assert.Equal(1, Sum("bsync.blob.transfers", ("bsync.direction", "upload"), ("bsync.result", "corrupt")));
        Assert.Equal(1, Sum("bsync.blob.transfers", ("bsync.direction", "download"), ("bsync.result", "complete")));
        Assert.Equal(3010, Sum("bsync.server.blob.bytes", ("bsync.direction", "upload")));
        Assert.Equal(3000, Sum("bsync.server.blob.bytes", ("bsync.direction", "download")));
        Assert.Equal(4, Sum("bsync.server.blob.requests", ("bsync.endpoint", "append"), ("bsync.result", "ok")));
        Assert.Equal(1, Sum("bsync.server.blob.requests", ("bsync.endpoint", "finish"), ("bsync.result", BlobCodes.HashMismatch)));
        Assert.Equal(1, Sum("bsync.server.blob.requests", ("bsync.endpoint", "read"), ("bsync.result", "ok")));
        Assert.DoesNotContain(measurements, m => m.Tags.Values.Any(v => v is string s && s.Contains(sha256, StringComparison.Ordinal))); // no content identifiers
    }

    private sealed class BytesSource(byte[] bytes) : IBlobSource
    {
        public long Length => bytes.Length;

        public ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, (int)offset, bytes.Length - (int)offset));
    }
}
