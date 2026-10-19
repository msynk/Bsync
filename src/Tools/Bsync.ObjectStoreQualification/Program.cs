using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Bsync.Blobs;
using Bsync.Samples.Tasks.Server;
using Bsync.Server.AspNetCore.Blobs;
using Microsoft.Playwright;

return await Qualification.RunAsync(args);

internal static class Qualification
{
    internal static void Require(bool condition, string code)
    {
        if (!condition) throw new CheckFailure(code);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] is not ("--emulator" or "--local-service")))
        {
            Console.Error.WriteLine("Usage: <new-evidence-directory> [--emulator|--local-service]; see README for environment configuration.");
            return 2;
        }
        Settings settings;
        try { settings = Settings.Load(args.Contains("--emulator"), args.Contains("--local-service")); }
        catch (Exception error) { Console.Error.WriteLine("Configuration rejected: " + SafeError(error)); return 2; }
        var output = Path.GetFullPath(args[0]);
        if (Directory.Exists(output) || File.Exists(output)) { Console.Error.WriteLine("Evidence path already exists."); return 2; }
        Directory.CreateDirectory(output);
        var prefix = "bsync-qualification/" + Guid.NewGuid().ToString("N") + "/";
        var local = Directory.CreateTempSubdirectory("bsync-objectstore-").FullName;
        var checks = new List<object>();
        var passed = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var s3 = new AmazonS3Client(settings.Credentials, new AmazonS3Config
        {
            ServiceURL = settings.Endpoint.AbsoluteUri, ForcePathStyle = true,
            AuthenticationRegion = settings.Region, Timeout = TimeSpan.FromSeconds(30), MaxErrorRetry = 1
        });
        var store = new S3BlobStore(s3, settings.Bucket, new FileSystemBlobStore(Path.Combine(local, "partials")), prefix)
        { PresignUploads = true, PresignHttp = settings.Endpoint.Scheme == "http" };
        var directGuard = new CredentialGuard();
        using var direct = new HttpClient(directGuard) { Timeout = TimeSpan.FromSeconds(30) };
        var bytes = new byte[6 * 1024 * 1024 + 17];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var authority = new TicketAuthority(store, hash, bytes.Length);
        using var authenticated = new HttpClient(authority) { BaseAddress = new Uri("https://qualification.invalid/") };
        authenticated.DefaultRequestHeaders.Authorization = new("Bearer", "qualification-only");
        var transfer = new HttpBlobTransfer(authenticated, new() { DownloadTickets = true, DirectClient = direct, ChunkSize = 1048576 });
        var source = new FileBlobCache(Path.Combine(local, "source"));
        var destination = new FileBlobCache(Path.Combine(local, "destination"));
        var safeToWrite = false;
        try
        {
            await Check("unversioned-test-bucket", async () =>
            {
                var versioning = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = settings.Bucket }, deadline.Token);
                Require(versioning.VersioningConfig?.Status?.Value is null or "Off", "test-bucket-must-never-have-versioning-enabled");
                safeToWrite = true;
            });
            if (safeToWrite)
            {
                await Check("direct-upload-lost-completion-reconciliation", async () =>
                {
                    await source.ImportAsync(new MemoryStream(bytes), deadline.Token);
                    var lost = false;
                    try { await transfer.UploadAsync(source, hash, deadline.Token); }
                    catch (LostAcknowledgement) { lost = true; }
                    Require(lost && await store.ExistsAsync(hash, deadline.Token), "completion-was-not-durable-before-loss");
                    Require(await transfer.UploadAsync(source, hash, deadline.Token) == 0, "completion-replay-resent-bytes");
                    Require(directGuard.PutRequests == 1, "unexpected-direct-upload-count");
                    Require(await store.GetReceivedAsync(authority.UploadId, deadline.Token) == 0, "completed-staging-remains");
                    await using var read = await store.OpenReadAsync(hash, deadline.Token);
                    Require(read is not null && Convert.ToHexStringLower(await SHA256.HashDataAsync(read, deadline.Token)) == hash, "stored-hash-mismatch");
                });
                Uri? expired = null;
                await Check("service-enforces-ticket-expiry", async () =>
                {
                    expired = await store.PresignReadAsync(hash, TimeSpan.FromSeconds(2), deadline.Token);
                    await Task.Delay(TimeSpan.FromSeconds(4), deadline.Token);
                    using var denied = await direct.GetAsync(expired, deadline.Token);
                    Require(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, "expired-ticket-was-not-denied");
                });
                await Check("resumed-range-and-credential-isolation", async () =>
                {
                    Require(expired is not null, "missing-expired-ticket");
                    authority.FirstDownload = expired;
                    await destination.AppendPartialAsync(hash, bytes.AsMemory(0, 2 * 1048576), deadline.Token);
                    var received = await transfer.DownloadAsync(destination, hash, bytes.Length, deadline.Token);
                    Require(received == bytes.Length - 2 * 1048576, "resume-count-mismatch");
                    Require(directGuard.RangeRequests >= 1 && directGuard.CredentialLeaks == 0, "direct-credential-or-range-violation");
                    await using var read = await destination.OpenReadAsync(hash, deadline.Token);
                    Require(read is not null && Convert.ToHexStringLower(await SHA256.HashDataAsync(read, deadline.Token)) == hash, "resumed-hash-mismatch");
                });
                await Check("expired-ticket-refreshed-exactly-once", () =>
                {
                    Require(authority.DownloadTickets == 2 && directGuard.RangeRequests == 2, "ticket-refresh-count-mismatch");
                    return Task.CompletedTask;
                });
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls(settings.Origin);
                await using var origin = builder.Build();
                origin.MapGet("/", () => Results.Content("<!doctype html><title>Bsync CORS qualification</title>", "text/html"));
                await Check("browser-origin-startup", () => origin.StartAsync(deadline.Token));
                foreach (var engine in new[] { "chromium", "firefox", "webkit" })
                    await Check("cors-put-get-range-" + engine, () => BrowserAsync(engine, settings.Origin, store, deadline.Token));
                await Check("orphan-collection-preserves-completed-object", async () =>
                {
                    var upload = "orphan-" + Guid.NewGuid().ToString("N");
                    var ticket = await store.PresignWriteAsync(upload, 3, TimeSpan.FromMinutes(1), deadline.Token);
                    using var response = await direct.PutAsync(ticket, new ByteArrayContent([1, 2, 3]), deadline.Token);
                    Require(response.IsSuccessStatusCode, "orphan-upload-failed");
                    Require(await store.GetReceivedAsync(upload, deadline.Token) == 3, "orphan-not-observable");
                    Require(await store.PurgePartialsAsync(DateTimeOffset.UtcNow.AddMinutes(1), deadline.Token) >= 1, "orphan-not-collected");
                    Require(await store.GetReceivedAsync(upload, deadline.Token) == 0 && await store.ExistsAsync(hash, deadline.Token), "orphan-cleanup-damaged-completed-content");
                });
            }
        }
        catch (Exception error) { passed = false; checks.Add(new { Name = "harness", Passed = false, Error = SafeError(error) }); }
        finally
        {
            // Only this run's newly generated prefix is eligible. Never mutate bucket configuration or other keys.
            if (safeToWrite) await Check("isolated-prefix-cleanup", async () =>
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var objects = await s3.ListObjectsV2Async(new() { BucketName = settings.Bucket, Prefix = prefix, MaxKeys = 100 }, cleanup.Token);
                Require(objects.IsTruncated != true, "unexpected-cleanup-inventory-size");
                foreach (var item in objects.S3Objects ?? [])
                {
                    Require(item.Key.StartsWith(prefix, StringComparison.Ordinal), "cleanup-key-outside-run-prefix");
                    await s3.DeleteObjectAsync(settings.Bucket, item.Key, cleanup.Token);
                }
                var remaining = await s3.ListObjectsV2Async(new() { BucketName = settings.Bucket, Prefix = prefix, MaxKeys = 1 }, cleanup.Token);
                Require((remaining.S3Objects?.Count ?? 0) == 0, "cleanup-left-objects");
            });
            await Check("local-snapshot-cleanup", () =>
            {
                var resolved = Path.GetFullPath(local);
                Require(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("bsync-objectstore-", StringComparison.Ordinal), "cleanup-path-outside-temporary-root");
                Directory.Delete(resolved, true);
                return Task.CompletedTask;
            });
            var report = new
            {
                Schema = 1, Timestamp = DateTimeOffset.UtcNow, Passed = passed, ExternalServiceQualified = passed && !settings.Emulator && !settings.LocalService,
                LocalServiceQualified = passed && settings.LocalService, settings.Emulator, settings.LocalService,
                Endpoint = settings.Endpoint.GetLeftPart(UriPartial.Authority), settings.Bucket, settings.Region,
                BrowserOrigin = settings.Origin, Prefix = prefix, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, Checks = checks,
                Assemblies = new[] { typeof(Qualification).Assembly, typeof(HttpBlobTransfer).Assembly, typeof(FileSystemBlobStore).Assembly }
                    .Select(a => new { Name = a.GetName().Name, Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a.Location))) }),
                Limits = new[] { "Synthetic in-process authority; tenant authorization and SQL transaction qualification remain separate.",
                    "Browser engines are desktop Playwright, not physical devices or native Safari.",
                    "Emulator and loopback local-service runs never qualify an external deployment, even if every check passes.",
                    "Only the selected endpoint, bucket policy and these presigned GET/PUT capabilities were exercised." }
            };
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;

        async Task Check(string name, Func<Task> action)
        {
            var timer = Stopwatch.StartNew();
            try { await action(); checks.Add(new { Name = name, Passed = true, Milliseconds = timer.ElapsedMilliseconds }); Console.WriteLine(name + ": PASS"); }
            catch (Exception error) { passed = false; var safe = SafeError(error); checks.Add(new { Name = name, Passed = false, Error = safe, Milliseconds = timer.ElapsedMilliseconds }); Console.WriteLine(name + ": FAIL " + safe); }
        }
    }

    private static string SafeError(Exception error) => error is CheckFailure failure ? failure.Code :
        error is AmazonS3Exception service ? $"AmazonS3Exception HTTP {(int)service.StatusCode}" : error.GetType().Name;

    private static async Task BrowserAsync(string engine, string origin, S3BlobStore store, CancellationToken token)
    {
        var phase = "startup";
        var responses = new List<string>();
        try
        {
        using var playwright = await Playwright.CreateAsync();
        var type = engine switch { "chromium" => playwright.Chromium, "firefox" => playwright.Firefox, _ => playwright.Webkit };
        await using var browser = await type.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        page.Response += (_, response) => responses.Add(response.Request.Method + ":" + response.Status);
        page.RequestFailed += (_, request) => responses.Add(request.Method + ":failed");
        page.SetDefaultTimeout(20000);
        await page.GotoAsync(origin);
        var upload = "browser-" + Guid.NewGuid().ToString("N");
        var ticket = await store.PresignWriteAsync(upload, 1048576, TimeSpan.FromMinutes(2), token);
        phase = "put";
        var status = await page.EvaluateAsync<int>("""
            async url => {
              const bytes = Uint8Array.from({length: 1048576}, (_, i) => i % 251);
              const response = await fetch(url, {method: 'PUT', credentials: 'omit',
                headers: {'Content-Type': 'application/octet-stream'}, body: bytes, signal: AbortSignal.timeout(20000)});
              return response.status;
            }
            """, ticket!.AbsoluteUri);
        Require(status is >= 200 and < 300, "browser-cors-put-refused");
        var bytes = new byte[1048576]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Require(await store.CompleteAsync(upload, hash, token), "browser-upload-verification-failed");
        var get = await store.PresignReadAsync(hash, TimeSpan.FromMinutes(2), token);
        using (var probe = new HttpClient())
        using (var request = new HttpRequestMessage(HttpMethod.Get, get))
        {
            request.Headers.Add("Origin", origin);
            using var response = await probe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            Require(!response.Content.Headers.ContentEncoding.Contains("aws-chunked", StringComparer.OrdinalIgnoreCase),
                "stored-object-retains-transfer-encoding");
        }
        phase = "get-range";
        var result = await page.EvaluateAsync<JsonElement>("""
            async url => {
              const full = await fetch(url, {credentials: 'omit', signal: AbortSignal.timeout(20000)});
              const body = await full.arrayBuffer();
              const hash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', body))].map(x => x.toString(16).padStart(2,'0')).join('');
              const range = await fetch(url, {credentials: 'omit', headers: {'Range': 'bytes=1048500-1048575'}, signal: AbortSignal.timeout(20000)});
              const tail = new Uint8Array(await range.arrayBuffer());
              return {status: full.status, hash, rangeStatus: range.status, contentRange: range.headers.get('Content-Range'),
                correctTail: tail.length === 76 && tail.every((x,i) => x === (1048500+i)%251)};
            }
            """, get!.AbsoluteUri);
        Require(result.GetProperty("status").GetInt32() == 200 && result.GetProperty("hash").GetString() == hash, "browser-full-hash-mismatch");
        Require(result.GetProperty("rangeStatus").GetInt32() == 206 && result.GetProperty("contentRange").GetString() == "bytes 1048500-1048575/1048576" &&
            result.GetProperty("correctTail").GetBoolean(), "browser-range-or-exposed-header-mismatch");
        }
        catch (PlaywrightException error)
        {
            // Static phase/method/status only: Playwright messages may contain signed URLs or request bodies.
            throw new CheckFailure($"browser-{phase}-{error.GetType().Name}-responses-{string.Join(',', responses)}");
        }
    }

    private sealed class CredentialGuard : DelegatingHandler
    {
        public int PutRequests, RangeRequests, CredentialLeaks;
        public CredentialGuard() : base(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Headers.Authorization is not null || request.Headers.Contains("Cookie")) { CredentialLeaks++; throw new CheckFailure("authority-credentials-reached-direct-client"); }
            if (request.Method == HttpMethod.Put) PutRequests++;
            if (request.Headers.Range is not null) RangeRequests++;
            return base.SendAsync(request, token);
        }
    }

    private sealed class TicketAuthority(S3BlobStore store, string hash, int size) : HttpMessageHandler
    {
        public string UploadId { get; } = "native-" + Guid.NewGuid().ToString("N");
        public Uri? FirstDownload;
        public int DownloadTickets;
        private bool _loseAcknowledgement = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.Headers.Authorization?.Parameter == "qualification-only", "authority-credential-missing");
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == $"/sync/blobs/{hash}/download" && request.Method == HttpMethod.Get)
            {
                DownloadTickets++;
                body = new { schema = 1, downloadUrl = (DownloadTickets == 1 ? FirstDownload : await store.PresignReadAsync(hash, TimeSpan.FromMinutes(2), token))!.AbsoluteUri };
            }
            else if (path == $"/sync/blobs/{hash}/uploads/finish" && request.Method == HttpMethod.Post)
            {
                Require(await store.CompleteAsync(UploadId, hash, token), "completion-verification-failed");
                if (_loseAcknowledgement) { _loseAcknowledgement = false; throw new LostAcknowledgement(); }
                body = new { received = size, complete = true };
            }
            else
            {
                Require(path == $"/sync/blobs/{hash}/uploads" && request.Method == HttpMethod.Post, "unexpected-authority-route");
                var complete = await store.ExistsAsync(hash, token);
                body = new { received = complete ? size : await store.GetReceivedAsync(UploadId, token), complete,
                    uploadUrl = complete ? null : (await store.PresignWriteAsync(UploadId, size, TimeSpan.FromMinutes(2), token))!.AbsoluteUri };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class LostAcknowledgement : IOException;
    private sealed class CheckFailure(string code) : Exception { public string Code => code; }
    private sealed record Settings(Uri Endpoint, string Bucket, string Region, string Origin, AWSCredentials Credentials, bool Emulator, bool LocalService)
    {
        public static Settings Load(bool emulator, bool localService)
        {
            static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : throw new CheckFailure("missing-" + key);
            var endpoint = new Uri(Required("BSYNC_OBJECTSTORE_ENDPOINT"));
            Require(endpoint.IsAbsoluteUri && endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 &&
                (endpoint.Scheme == "https" || (emulator || localService) && endpoint.IsLoopback && endpoint.Scheme == "http"), "invalid-service-endpoint");
            Require(!emulator || endpoint.IsLoopback, "emulator-must-be-loopback");
            Require(!localService || endpoint.IsLoopback, "local-service-must-be-loopback");
            var port = int.Parse(Environment.GetEnvironmentVariable("BSYNC_OBJECTSTORE_ORIGIN_PORT") ?? "58991");
            Require(port is >= 1024 and <= 65535, "invalid-origin-port");
            var access = Required("AWS_ACCESS_KEY_ID"); var secret = Required("AWS_SECRET_ACCESS_KEY");
            var session = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");
            AWSCredentials credentials = string.IsNullOrEmpty(session) ? new BasicAWSCredentials(access, secret) : new SessionAWSCredentials(access, secret, session);
            return new(endpoint, Required("BSYNC_OBJECTSTORE_BUCKET"), Required("AWS_REGION"), $"http://127.0.0.1:{port}", credentials, emulator, localService);
        }
    }
}
