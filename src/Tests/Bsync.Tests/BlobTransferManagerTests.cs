using System.Net;
using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

public sealed class BlobTransferManagerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bsync-jobs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly byte[] _bytes = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
    private string Hash => Convert.ToHexStringLower(SHA256.HashData(_bytes));
    public Task InitializeAsync() { Directory.CreateDirectory(_directory); return Task.CompletedTask; }
    public async Task DisposeAsync()
    {
        await SqliteStorePool.DeleteDatabaseAsync(Path.Combine(_directory, "jobs.db"));
        Directory.Delete(_directory, true);
    }
    private Task<SqliteOrchestrationStore> Store() => SqliteOrchestrationStore.OpenAsync(Path.Combine(_directory, "jobs.db"), new("test", "test", "alice"), timeProvider: _time);
    private FileBlobCache Cache() => new(Path.Combine(_directory, "cache"));
    private BlobTransferManager Manager(IOrchestrationStore store, HttpMessageHandler handler, string endpoint = "https://example.test/", int scanLimit = 1000) =>
        new(store, Cache(), new(new HttpClient(handler) { BaseAddress = new(endpoint) }, new() { ChunkSize = 1024 }),
            new() { TimeProvider = _time, ScanLimit = scanLimit });

    [Fact]
    public async Task ReadyToPushRevalidatesHistoricalCompletionAndPreservesExplicitPause()
    {
        await using var store = await Store();
        await Cache().ImportAsync(new MemoryStream(_bytes));
        var starts = 0; var available = true;
        var manager = Manager(store, new Handler((_, _) =>
        {
            starts++;
            return Task.FromResult(available
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"received\":{_bytes.Length},\"complete\":true}}") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        BlobReference[] required = [new("file", Hash, _bytes.Length, "text/plain", "file.txt")];
        Assert.True(await manager.EnsureUploadsAsync(required, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, starts);
        available = false;
        Assert.False(await manager.EnsureUploadsAsync(required, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, starts);
        Assert.Equal(BlobTransferState.WaitingForCredentials, (await manager.GetAsync(Hash, BlobTransferDirection.Upload))!.State);
        available = true;
        Assert.True(await manager.ResumeCredentialWaitAsync(Hash, BlobTransferDirection.Upload));
        Assert.False(await manager.ResumeCredentialWaitAsync(Hash, BlobTransferDirection.Upload));
        Assert.True(await manager.EnsureUploadsAsync(required, TimeSpan.FromSeconds(5)));
        available = false;
        Assert.False(await manager.EnsureUploadsAsync(required, TimeSpan.FromSeconds(5)));
        await manager.SetStateAsync(Hash, BlobTransferDirection.Upload, BlobTransferState.Paused);
        available = true;
        Assert.False(await manager.ResumeCredentialWaitAsync(Hash, BlobTransferDirection.Upload));
        Assert.False(await manager.EnsureUploadsAsync(required, TimeSpan.FromSeconds(5)));
        Assert.Equal(4, starts);
    }

    [Fact]
    public async Task DownloadTicketsRefreshExpiredUrlsWithoutForwardingCredentialsAndResumeFromDisk()
    {
        var tickets = 0; var downloads = 0;
        using var application = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("secret", request.Headers.GetValues("X-App-Credential").Single());
            Assert.EndsWith("/download", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"schema\":1,\"downloadUrl\":\"https://bucket.test/content?ticket={++tickets}\"}}") });
        })) { BaseAddress = new("https://application.test/") };
        application.DefaultRequestHeaders.Add("X-App-Credential", "secret");
        using var bucket = new HttpClient(new Handler((request, _) =>
        {
            Assert.False(request.Headers.Contains("X-App-Credential"));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(1536, request.Headers.Range!.Ranges.Single().From);
            if (++downloads == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_bytes[1536..]) };
            response.Content.Headers.ContentRange = new(1536, _bytes.Length - 1, _bytes.Length);
            return Task.FromResult(response);
        }));
        await Cache().AppendPartialAsync(Hash, _bytes.AsMemory(0, 1536));
        var transfer = new HttpBlobTransfer(application, new() { DownloadTickets = true, DirectClient = bucket, ChunkSize = 1024 });
        Assert.Equal(_bytes.Length - 1536, await transfer.DownloadAsync(Cache(), Hash, _bytes.Length));
        Assert.Equal(2, tickets); Assert.Equal(2, downloads);
        Assert.Equal(_bytes.Length, await Cache().SizeAsync(Hash));
    }

    [Fact]
    public async Task UnsupportedTicketEndpointAndRepeatedExpiryNeverFallBackToCredentialedRedirects()
    {
        var requests = 0;
        using var application = new HttpClient(new Handler((request, _) =>
        {
            requests++; Assert.EndsWith("/download", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        })) { BaseAddress = new("https://old-server.test/") };
        var transfer = new HttpBlobTransfer(application, new() { DownloadTickets = true });
        await Assert.ThrowsAsync<HttpRequestException>(() => transfer.DownloadAsync(Cache(), Hash, _bytes.Length));
        Assert.Equal(1, requests);
        Assert.Equal(0, await Cache().PartialLengthAsync(Hash));
        using var current = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"schema\":1,\"downloadUrl\":\"https://bucket.test/expired\"}") }))) { BaseAddress = new("https://new-server.test/") };
        var bucketRequests = 0;
        using var bucket = new HttpClient(new Handler((_, _) => { bucketRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }));
        await Assert.ThrowsAsync<IOException>(() => new HttpBlobTransfer(current, new() { DownloadTickets = true, DirectClient = bucket }).DownloadAsync(Cache(), Hash, _bytes.Length));
        Assert.Equal(2, bucketRequests);
    }

    [Fact]
    public async Task WrongContentRangeAndOversizedBodyNeverBecomeVerifiedContent()
    {
        await Cache().AppendPartialAsync(Hash, _bytes.AsMemory(0, 100));
        using var wrongRange = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_bytes[100..]) };
            response.Content.Headers.ContentRange = new(0, _bytes.Length - 101, _bytes.Length);
            return Task.FromResult(response);
        })) { BaseAddress = new("https://server.test/") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new HttpBlobTransfer(wrongRange).DownloadAsync(Cache(), Hash, _bytes.Length));
        Assert.Equal(100, await Cache().PartialLengthAsync(Hash));
        await Cache().DiscardPartialAsync(Hash);
        using var oversized = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([.. _bytes, 9]) }))) { BaseAddress = new("https://server.test/") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new HttpBlobTransfer(oversized).DownloadAsync(Cache(), Hash, _bytes.Length));
        Assert.Null(await Cache().SizeAsync(Hash));
    }

    [Fact]
    public async Task SameLengthCorruptionIsNotReportedVerifiedOrSilentlyDeleted()
    {
        await Cache().ImportAsync(new MemoryStream(_bytes));
        var path = Path.Combine(_directory, "cache", "objects", Hash[..2], Hash);
        await File.WriteAllBytesAsync(path, new byte[_bytes.Length]);
        var transfer = new HttpBlobTransfer(new HttpClient(new Handler((_, _) => throw new Exception("Unexpected network"))) { BaseAddress = new("https://offline.test/") });
        var progress = new List<BlobTransferProgress>();
        transfer.Progress += progress.Add;
        await Assert.ThrowsAsync<BlobCorruptedException>(() => transfer.DownloadAsync(Cache(), Hash, _bytes.Length));
        Assert.Empty(progress);
        Assert.Equal(_bytes.Length, await Cache().SizeAsync(Hash));
        Assert.All(await File.ReadAllBytesAsync(path), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ProgressDistinguishesDurablePartialFromVerifiedContentAndIsolatesObservers()
    {
        var truncate = true;
        var transfer = new HttpBlobTransfer(new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(truncate ? _bytes[..1536] : _bytes[1536..]) }))) { BaseAddress = new("https://example.test/") }, new() { ChunkSize = 1024 });
        var progress = new List<BlobTransferProgress>();
        transfer.Progress += _ => throw new InvalidOperationException("UI failure");
        transfer.Progress += progress.Add;
        await Assert.ThrowsAsync<IOException>(() => transfer.DownloadAsync(Cache(), Hash, _bytes.Length));
        Assert.Equal(1536, progress[^1].AvailableBytes);
        Assert.DoesNotContain(progress, p => p.Verified);
        truncate = false; progress.Clear();
        await transfer.DownloadAsync(Cache(), Hash, _bytes.Length);
        Assert.Equal(1536, progress[0].AvailableBytes);
        Assert.True(progress[^1].Verified);
        Assert.Equal(_bytes.Length, progress[^1].AvailableBytes);
        Assert.Single(progress, p => p.Verified);
        await Assert.ThrowsAsync<BlobCorruptedException>(() => transfer.DownloadAsync(Cache(), Hash, _bytes.Length + 1));
        Assert.Equal(_bytes.Length, await Cache().SizeAsync(Hash));
    }

    [Fact]
    public async Task InterruptedDownloadResumesAfterReconstructionFromDurableOffset()
    {
        await using (var store = await Store())
        {
            var first = Manager(store, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes[..1536]) })));
            await first.EnqueueAsync(Hash, _bytes.Length, BlobTransferDirection.Download);
            Assert.Equal(0, (await first.DrainAsync(TimeSpan.FromSeconds(5))).Completed);
            Assert.Equal(1536, await Cache().PartialLengthAsync(Hash));
            Assert.Equal(BlobTransferState.WaitingForConnectivity, (await first.GetAsync(Hash, BlobTransferDirection.Download))!.State);
        }
        _time.Advance(TimeSpan.FromSeconds(3));
        await using var reopened = await Store();
        var resumed = Manager(reopened, new Handler((request, _) =>
        {
            Assert.Equal(1536, request.Headers.Range!.Ranges.Single().From);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_bytes[1536..]) });
        }));
        Assert.Equal(1, (await resumed.DrainAsync(TimeSpan.FromSeconds(5))).Completed);
        Assert.Equal(BlobTransferState.Complete, (await resumed.GetAsync(Hash, BlobTransferDirection.Download))!.State);
        await using var content = await Cache().OpenReadAsync(Hash);
        Assert.Equal(Hash, Convert.ToHexStringLower(await SHA256.HashDataAsync(content!)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BlobTransferState.WaitingForCredentials)]
    [InlineData(HttpStatusCode.Forbidden, BlobTransferState.WaitingForCredentials)]
    [InlineData(HttpStatusCode.NotFound, BlobTransferState.Failed)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, BlobTransferState.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BlobTransferState.WaitingForConnectivity)]
    public async Task FailuresAreDurableAndClassified(HttpStatusCode status, BlobTransferState expected)
    {
        await using var store = await Store();
        var manager = Manager(store, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        await manager.EnqueueAsync(Hash, _bytes.Length, BlobTransferDirection.Download);
        Assert.True((await manager.DrainAsync(TimeSpan.FromSeconds(5))).HasRemainingWork);
        Assert.Equal(expected, (await manager.GetAsync(Hash, BlobTransferDirection.Download))!.State);
    }

    [Fact]
    public async Task ConcurrentCancellationWinsOverLateResponseAndOtherOwnerCannotDrain()
    {
        await using var store = await Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(); });
        var manager = Manager(store, handler);
        await manager.EnqueueAsync(Hash, _bytes.Length, BlobTransferDirection.Download);
        var drain = manager.DrainAsync(TimeSpan.FromSeconds(5));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await Manager(store, handler).DrainAsync(TimeSpan.FromSeconds(5))).Owned);
        await manager.SetStateAsync(Hash, BlobTransferDirection.Download, BlobTransferState.Canceled);
        await drain;
        Assert.Equal(BlobTransferState.Canceled, (await manager.GetAsync(Hash, BlobTransferDirection.Download))!.State);
        Assert.Null(await Cache().SizeAsync(Hash));
    }

    [Fact]
    public async Task JobsDeduplicateWithinEndpointAndScanRotatesPastTerminalRecords()
    {
        await using var store = await Store();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) }));
        var manager = Manager(store, handler, scanLimit: 1);
        var firstHash = new string('0', 64);
        await manager.EnqueueAsync(firstHash, 1, BlobTransferDirection.Download);
        await manager.SetStateAsync(firstHash, BlobTransferDirection.Download, BlobTransferState.Canceled);
        var job = await manager.EnqueueAsync(Hash, _bytes.Length, BlobTransferDirection.Download);
        Assert.Equal(job, await manager.EnqueueAsync(Hash, _bytes.Length, BlobTransferDirection.Download));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.EnqueueAsync(Hash, 1, BlobTransferDirection.Download));
        Assert.Null(await Manager(store, handler, "https://other.test/").GetAsync(Hash, BlobTransferDirection.Download));
        Assert.Equal(0, (await manager.DrainAsync(TimeSpan.FromSeconds(5))).Completed);
        Assert.Equal(1, (await manager.DrainAsync(TimeSpan.FromSeconds(5))).Completed);
    }

    [Fact]
    public async Task CorruptionNeverBecomesCompleteAndMissingUploadRequiresRepair()
    {
        await using var store = await Store();
        var manager = Manager(store, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[_bytes.Length]) })));
        await manager.EnqueueAsync(Hash, _bytes.Length, BlobTransferDirection.Download);
        await manager.EnqueueAsync(Hash, _bytes.Length, BlobTransferDirection.Upload);
        await manager.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hash-or-length-mismatch", (await manager.GetAsync(Hash, BlobTransferDirection.Download))!.ErrorCode);
        Assert.Equal("missing-local-content", (await manager.GetAsync(Hash, BlobTransferDirection.Upload))!.ErrorCode);
        Assert.Null(await Cache().SizeAsync(Hash));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task ExpiredPresignedUploadRefreshesWithoutForwardingApplicationCredentials()
    {
        var offers = 0; var puts = 0; var finished = false;
        using var application = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("app-secret", request.Headers.Authorization!.Parameter);
            var finish = request.RequestUri!.AbsolutePath.EndsWith("/finish", StringComparison.Ordinal);
            if (finish) finished = true; else offers++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(finish ? "{\"received\":4096,\"complete\":true}"
                    : "{\"received\":0,\"complete\":false,\"uploadUrl\":\"https://bucket.test/" + offers + "\"}")
            });
        })) { BaseAddress = new("https://app.test/") };
        application.DefaultRequestHeaders.Authorization = new("Bearer", "app-secret");
        using var bucket = new HttpClient(new Handler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            puts++;
            return Task.FromResult(new HttpResponseMessage(puts == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.OK));
        }));
        var cache = Cache();
        await cache.ImportAsync(new MemoryStream(_bytes));
        var transfer = new HttpBlobTransfer(application, new() { DirectClient = bucket });
        Assert.Equal(_bytes.Length, await transfer.UploadAsync(cache, Hash));
        Assert.Equal(2, offers); Assert.Equal(2, puts); Assert.True(finished);
    }

    [Fact]
    public async Task EmptyContentIsVerifiedWithoutNetwork()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
        var transfer = new HttpBlobTransfer(new HttpClient(new Handler((_, _) => throw new Exception("Unexpected network"))) { BaseAddress = new("https://test/") });
        Assert.Equal(0, await transfer.DownloadAsync(Cache(), hash, 0));
        Assert.Equal(0, await Cache().SizeAsync(hash));
    }
}
