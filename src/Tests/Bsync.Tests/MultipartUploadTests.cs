using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class MultipartUploadTests
{
    [Fact]
    public async Task ReconcilesAcceptedPartAfterLostReplyAndRetriesCompletionWithoutRetransmitting()
    {
        var root = Directory.CreateTempSubdirectory("bsync-multipart-").FullName;
        try
        {
            await using var store = new InMemoryOrchestrationStore();
            var authority = new Authority();
            using var direct = new HttpClient(authority);
            var source = new Source(new byte[5242880 + 17]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(source.Bytes));
            ResumableMultipartUpload Client() => new(store, new FileBlobCacheLock(root), authority, direct);
            await Assert.ThrowsAsync<HttpRequestException>(() => Client().UploadAsync(hash, source));
            Assert.Single(authority.Parts);
            authority.LosePartReply = false; authority.LoseCompleteReply = true;
            await Assert.ThrowsAsync<IOException>(() => Client().UploadAsync(hash, source));
            Assert.Equal(2, authority.Parts.Count);
            Assert.Equal(0, await Client().UploadAsync(hash, source));
            Assert.Equal(3, authority.Puts); // first accepted part, expired URL, final part
            Assert.Equal(1, authority.Completed);
            Assert.Equal(new long[] { 0, 5242880 }, source.Offsets);
            var durable = Assert.Single((await store.ReadPageAsync("multipart/", null, 10)));
            Assert.DoesNotContain("secret", durable.Payload);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AbortReconcilesLostInitiationReplyAndRetainsStateOnFailedAbort()
    {
        var root = Directory.CreateTempSubdirectory("bsync-multipart-").FullName;
        try
        {
            await using var store = new InMemoryOrchestrationStore();
            var authority = new Authority { LoseStartReply = true, FailAbort = true };
            using var direct = new HttpClient(authority);
            var client = new ResumableMultipartUpload(store, new FileBlobCacheLock(root), authority, direct);
            var hash = new string('a', 64);
            await Assert.ThrowsAsync<IOException>(() => client.UploadAsync(hash, new Source([1])));
            authority.LoseStartReply = false;
            await Assert.ThrowsAsync<IOException>(() => client.AbortAsync(hash));
            Assert.Single((await store.ReadPageAsync("multipart/", null, 10)));
            authority.FailAbort = false;
            await client.AbortAsync(hash);
            Assert.True(authority.Aborted);
            Assert.Empty((await store.ReadPageAsync("multipart/", null, 10)));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Source(byte[] bytes) : IBlobSource
    {
        public byte[] Bytes => bytes;
        public List<long> Offsets { get; } = [];
        public long Length => bytes.Length;
        public ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default)
        { Offsets.Add(offset); var stream = new MemoryStream(bytes, false); stream.Position = offset; return ValueTask.FromResult<Stream>(stream); }
    }
    private sealed class Authority : HttpMessageHandler, IMultipartUploadProtocol
    {
        public Uri Endpoint => new("https://authority.test/multipart/");
        public bool LoseStartReply, LosePartReply = true, LoseCompleteReply, FailAbort, Aborted;
        public int Puts, Completed;
        public List<MultipartPartReceipt> Parts { get; } = [];
        private string? _operation;
        private MultipartUploadSession _session = null!;
        public Task<MultipartUploadSession?> StartAsync(string operationId, string sha256, long size, CancellationToken cancellationToken)
        {
            Assert.True(_operation is null || _operation == operationId); _operation = operationId;
            _session ??= new(1, "session", sha256, size, 5242880, [], false);
            if (LoseStartReply) throw new IOException("Lost initiation reply.");
            return Task.FromResult<MultipartUploadSession?>(_session with { Parts = Parts.ToArray() });
        }
        public Task<MultipartPartTicket> AuthorizePartAsync(string sessionId, string sha256, int number, CancellationToken cancellationToken) =>
            Task.FromResult(new MultipartPartTicket(new Uri($"https://object.test/{number}?signature=secret"), number, (number - 1L) * 5242880, (int)Math.Min(5242880, _session.Size - (number - 1L) * 5242880)));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Puts++;
            if (Puts == 2) return new(HttpStatusCode.Forbidden);
            var number = int.Parse(request.RequestUri!.AbsolutePath.Trim('/'));
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            Parts.Add(new(number, (number - 1L) * 5242880, bytes.Length, "\"receipt\""));
            if (LosePartReply) throw new HttpRequestException("Lost https://object.test/?signature=secret");
            var result = new HttpResponseMessage(HttpStatusCode.OK); result.Headers.ETag = new EntityTagHeaderValue("\"receipt\""); return result;
        }
        public Task<MultipartUploadSession> CompleteAsync(string sessionId, string sha256, CancellationToken cancellationToken)
        {
            Completed++; _session = _session with { VerifiedComplete = true, Parts = Parts.ToArray() };
            if (LoseCompleteReply) throw new IOException("Lost completion reply.");
            return Task.FromResult(_session);
        }
        public Task AbortAsync(string sessionId, string sha256, CancellationToken cancellationToken)
        { if (FailAbort) throw new IOException("Abort unavailable."); Aborted = true; return Task.CompletedTask; }
    }
}
