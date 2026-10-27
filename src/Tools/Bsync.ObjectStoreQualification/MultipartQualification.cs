using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Samples.Tasks.Server;
using Bsync.Storage.Sqlite;
using Microsoft.Playwright;

internal static class MultipartQualification
{
    public static async Task RunAsync(IAmazonS3 s3, string bucket, string prefix, Uri endpoint, string origin,
        string local, Func<string, Func<Task>, Task> check, CancellationToken ct)
    {
        var account = new AccountScope("qualification", "multipart", "alice");
        var locks = new FileBlobCacheLock(Path.Combine(local, "multipart-locks"));
        var serverPath = Path.Combine(local, "multipart-server.db");
        var clientPath = Path.Combine(local, "multipart-client.db");
        var bytes = new byte[10 * 1024 * 1024 + 17];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 239);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var grants = new HashSet<string>(StringComparer.Ordinal);
        using var loss = new LoseAcceptedPart();
        using var direct = new HttpClient(loss);
        S3MultipartProtocol Provider(SqliteOrchestrationStore metadata, string scope = "generation-alice") => new(
            s3, bucket, prefix, scope, metadata, locks, endpoint, (h, _, _) => { grants.Add(h); return Task.CompletedTask; })
            { AllowHttp = endpoint.Scheme == "http" };
        await check("multipart-lost-part-durable-reopen-and-lost-completion", async () =>
        {
            await using (var server = await SqliteOrchestrationStore.OpenAsync(serverPath, account, cancellationToken: ct))
            await using (var client = await SqliteOrchestrationStore.OpenAsync(clientPath, account, cancellationToken: ct))
            {
                var transfer = new ResumableMultipartUpload(client, locks, Provider(server), direct);
                try { await transfer.UploadAsync(hash, new Source(bytes), cancellationToken: ct); throw new InvalidOperationException("Expected lost accepted part."); }
                catch (LostReply) { }
                Qualification.Require(loss.AcceptedPuts == 1 && !grants.Contains(hash), "part-loss-state-mismatch");
            }
            await using (var server = await SqliteOrchestrationStore.OpenAsync(serverPath, account, cancellationToken: ct))
            await using (var client = await SqliteOrchestrationStore.OpenAsync(clientPath, account, cancellationToken: ct))
            {
                var transfer = new ResumableMultipartUpload(client, locks, new LoseCompletion(Provider(server)), direct);
                try { await transfer.UploadAsync(hash, new Source(bytes), cancellationToken: ct); throw new InvalidOperationException("Expected lost completion."); }
                catch (LostReply) { }
                Qualification.Require(loss.AcceptedPuts == 2 && grants.Contains(hash), "reconciled-part-was-resent-or-not-verified");
            }
            await using (var server = await SqliteOrchestrationStore.OpenAsync(serverPath, account, cancellationToken: ct))
            await using (var client = await SqliteOrchestrationStore.OpenAsync(clientPath, account, cancellationToken: ct))
            {
                var transfer = new ResumableMultipartUpload(client, locks, Provider(server), direct);
                Qualification.Require(await transfer.UploadAsync(hash, new Source(bytes), cancellationToken: ct) == 0 && loss.AcceptedPuts == 2,
                    "completed-replay-resent-parts");
                using var content = await s3.GetObjectAsync(bucket, prefix + "objects/" + hash[..2] + "/" + hash, ct);
                Qualification.Require(content.ContentLength == bytes.Length && Convert.ToHexStringLower(await SHA256.HashDataAsync(content.ResponseStream, ct)) == hash,
                    "multipart-final-digest-mismatch");
            }
        });
        await using var metadata = await SqliteOrchestrationStore.OpenAsync(serverPath, account, cancellationToken: ct);
        var provider = Provider(metadata);
        await check("multipart-scope-corruption-and-abort", async () =>
        {
            var id = Guid.NewGuid().ToString("N"); var wrongHash = new string('a', 64);
            await provider.StartAsync(id, wrongHash, 4, ct);
            try { await Provider(metadata, "generation-bob").AuthorizePartAsync(id, wrongHash, 1, ct); throw new InvalidOperationException("Scope accepted."); }
            catch (KeyNotFoundException) { }
            var ticket = await provider.AuthorizePartAsync(id, wrongHash, 1, ct);
            using var put = await direct.PutAsync(ticket.Url, new ByteArrayContent([1, 2, 3, 4]), ct);
            Qualification.Require(put.IsSuccessStatusCode, "corrupt-part-put-failed");
            try { await provider.CompleteAsync(id, wrongHash, ct); throw new InvalidOperationException("Corrupt content accepted."); }
            catch (InvalidDataException) { }
            Qualification.Require(!grants.Contains(wrongHash), "corrupt-content-granted-access");
            await provider.AbortAsync(id, wrongHash, ct); await provider.AbortAsync(id, wrongHash, ct);
            try { await provider.StartAsync(id, wrongHash, 4, ct); throw new InvalidOperationException("Aborted session restarted."); }
            catch (InvalidDataException) { }
            var orphans = await s3.ListMultipartUploadsAsync(new Amazon.S3.Model.ListMultipartUploadsRequest() { BucketName = bucket, Prefix = prefix }, ct);
            Qualification.Require((orphans.MultipartUploads?.Count ?? 0) == 0, "aborted-upload-remains");
        });
        using var playwright = await Playwright.CreateAsync();
        foreach (var engine in new[] { playwright.Chromium, playwright.Firefox, playwright.Webkit })
            await check("multipart-cors-etag-" + engine.Name, async () =>
            {
                var id = Guid.NewGuid().ToString("N");
                var smallHash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 7, 8, 9, 10 }));
                await provider.StartAsync(id, smallHash, 4, ct);
                try
                {
                    var ticket = await provider.AuthorizePartAsync(id, smallHash, 1, ct);
                    await using var browser = await engine.LaunchAsync(new() { Headless = true });
                    var page = await browser.NewPageAsync(); await page.GotoAsync(origin);
                    var etag = await page.EvaluateAsync<string?>("async url => { const r = await fetch(url, {method:'PUT', credentials:'omit', redirect:'error', body:new Uint8Array([7,8,9,10])}); if (!r.ok) return null; return r.headers.get('ETag'); }", ticket.Url.AbsoluteUri);
                    Qualification.Require(!string.IsNullOrWhiteSpace(etag), "multipart-cors-etag-unavailable");
                    var session = await provider.StartAsync(id, smallHash, 4, ct);
                    Qualification.Require(session!.Parts.Length == 1 && session.Parts[0].Receipt == etag, "browser-receipt-differs-from-service");
                    Qualification.Require((await provider.CompleteAsync(id, smallHash, ct)).VerifiedComplete, "browser-multipart-not-verified");
                }
                finally { await provider.AbortAsync(id, smallHash, CancellationToken.None); }
            });
    }
    private sealed class Source(byte[] bytes) : IBlobSource
    {
        public long Length => bytes.Length;
        public ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, (int)offset, bytes.Length - (int)offset, false));
    }
    private sealed class LostReply : IOException;
    private sealed class LoseAcceptedPart : DelegatingHandler
    {
        public int AcceptedPuts;
        public LoseAcceptedPart() : base(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Qualification.Require(!request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie"), "multipart-direct-credential-leak");
            var response = await base.SendAsync(request, ct);
            if (request.Method == HttpMethod.Put && response.IsSuccessStatusCode && ++AcceptedPuts == 1)
            { response.Dispose(); throw new LostReply(); }
            return response;
        }
    }
    private sealed class LoseCompletion(IMultipartUploadProtocol inner) : IMultipartUploadProtocol
    {
        public Uri Endpoint => inner.Endpoint;
        public Task<MultipartUploadSession?> StartAsync(string operationId, string sha256, long size, CancellationToken cancellationToken) => inner.StartAsync(operationId, sha256, size, cancellationToken);
        public Task<MultipartPartTicket> AuthorizePartAsync(string sessionId, string sha256, int number, CancellationToken cancellationToken) => inner.AuthorizePartAsync(sessionId, sha256, number, cancellationToken);
        public async Task<MultipartUploadSession> CompleteAsync(string sessionId, string sha256, CancellationToken cancellationToken)
        { await inner.CompleteAsync(sessionId, sha256, cancellationToken); throw new LostReply(); }
        public Task AbortAsync(string sessionId, string sha256, CancellationToken cancellationToken) => inner.AbortAsync(sessionId, sha256, cancellationToken);
    }
}
