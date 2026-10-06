using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Bsync.Client;
using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Console;
using Bsync.Samples.Tasks.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>Task B5: the relational sample end to end, with a real SQL Server, Kestrel and SQLite replicas.</summary>
public sealed class TasksSampleTests : IAsyncLifetime
{
    private readonly string _data = Directory.CreateTempSubdirectory("bsync-tasks-").FullName;
    private SqlServerDatabase _database = null!;

    public async Task InitializeAsync() => _database = await SqlServerDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        Directory.Delete(_data, recursive: true);
    }

    private static Uri FreeAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
    }

    private Task<WebApplication> StartServerAsync(Uri address, IReadOnlyDictionary<string, string>? settings = null) => TasksServer.BuildAsync([], builder =>
    {
        builder.WebHost.UseUrls(address.ToString());
        builder.Configuration["ConnectionStrings:Tasks"] = _database.ConnectionString;
        builder.Configuration["Tasks:BlobDirectory"] = Path.Combine(_data, "server-blobs");
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.Configuration[key] = value;
        }

        builder.Logging.ClearProviders();
    });

    [Fact(DisplayName = "B5 I01 I16 I19: offline write, restart, upload, server-computed field, rejection code, and a publisher write reaching a second client")]
    public async Task EndToEnd()
    {
        var address = FreeAddress();
        var alicesData = Path.Combine(_data, "alice");

        // 1. Offline: the server is not running. The save succeeds locally and stays pending.
        await using (var offline = TasksClient.Create(address, alicesData, "alice", "team-1"))
        {
            var saved = await offline.Tasks.SaveAsync(new TaskDocument { Id = "t1", Title = "Buy milk!" });
            Assert.Equal(SyncConfirmation.SavedLocally, saved.Confirmation);
            await Assert.ThrowsAnyAsync<Exception>(() => offline.SyncNowAsync());
            Assert.Equal(SyncItemState.Pending, (await offline.Tasks.GetItemStatusAsync("t1"))!.State);
        }

        // 2. The app restarts later, when the server is up: the pending write is uploaded from the SQLite replica.
        await using var server = await StartServerAsync(address);
        await server.StartAsync();
        await using var alice = TasksClient.Create(address, alicesData, "alice", "team-1");
        await alice.SyncNowAsync(); // the session's own loop may already have uploaded it; either way it is done now

        Assert.Equal(SyncItemState.Synced, (await alice.Tasks.GetItemStatusAsync("t1"))!.State);
        Assert.Equal("buy-milk", (await alice.Tasks.GetAsync("t1"))!.Slug); // computed by the server's write handler
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't1' AND Slug = 'buy-milk' AND ChangedBy = 'alice'"));

        // 3. A rule the server enforces: the rejection code is kept on the record and visible to the UI.
        await alice.Tasks.SaveAsync(new TaskDocument { Id = "t2", Title = "   " });
        await alice.SyncNowAsync();
        var status = await alice.Tasks.GetItemStatusAsync("t2");

        Assert.Equal(SyncItemState.Rejected, status!.State);
        Assert.Equal(TaskRules.TitleRequired, status.Detail);
        Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't2'"));

        // 4. The back office creates a task through an ordinary API endpoint (EF Core and the publisher in one transaction).
        using var http = new HttpClient { BaseAddress = address };
        var token = await (await http.PostAsJsonAsync("api/token", new TokenRequest("manager", "team-1"), TasksJson.Default.TokenRequest)).Content.ReadFromJsonAsync(TasksJson.Default.TokenResponse);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token);
        var created = await http.PostAsJsonAsync("api/tasks", new NewTask("t3", "Quarterly review"), TasksJson.Default.NewTask);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // 5. A second client of the same team receives both the replicated and the published task; another team sees none.
        await using var bob = TasksClient.Create(address, Path.Combine(_data, "bob"), "bob", "team-1");
        await bob.SyncNowAsync();
        await using var eve = TasksClient.Create(address, Path.Combine(_data, "eve"), "eve", "team-2");
        await eve.SyncNowAsync();
        await alice.SyncNowAsync();

        Assert.Equal(["t1=buy-milk", "t3=quarterly-review"], (await bob.Tasks.QueryAsync()).Select(t => $"{t.Id}={t.Slug}").Order());
        Assert.Contains(await alice.Tasks.QueryAsync(), t => t.Id == "t3");
        Assert.Empty(await eve.Tasks.QueryAsync());
        Assert.Equal(2, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Tenant = 'team-1'"));
    }

    [Fact(DisplayName = "F3 I01 I16 I19: an intent stays pending across a restart, executes once despite a lost response, waits for its task, and a rejection keeps its code")]
    public async Task IntentsExecuteOnce()
    {
        var address = FreeAddress();
        var alicesData = Path.Combine(_data, "alice");
        var first = await StartServerAsync(address);
        await first.StartAsync();
        await using (var setup = TasksClient.Create(address, alicesData, "alice", "team-1"))
        {
            await setup.Tasks.SaveAsync(new TaskDocument { Id = "t1", Title = "Write report" });
            await setup.SyncNowAsync();
            Assert.Equal(1, (await setup.Tasks.GetAsync("t1"))!.Revision);
        }

        await first.StopAsync();
        await first.DisposeAsync();

        // 1. Offline, the user completes the task: the intent is saved locally and is still pending after a restart.
        string completion;
        await using (var offline = TasksClient.Create(address, alicesData, "alice", "team-1"))
        {
            completion = (await offline.CompleteAsync("t1")).Id;
            await Assert.ThrowsAnyAsync<Exception>(() => offline.SyncNowAsync());
        }

        await using (var restarted = TasksClient.Create(address, alicesData, "alice", "team-1"))
        {
            var pending = await restarted.Intents.GetAsync(completion);
            Assert.Equal((IntentStates.Pending, 1L), (pending!.State, pending.TaskRevision));
            Assert.Equal(SyncItemState.Pending, (await restarted.Intents.GetItemStatusAsync(completion))!.State);
        }

        // 2. The server is back, and the response to the intent's upload is lost after the server committed it. The
        //    replica sends the same operation again; the receipt answers it, and the task is changed once.
        await using var server = await StartServerAsync(address);
        await server.StartAsync();
        var lost = new LostResponses($"collections/{TasksJson.IntentCollection}/push");
        await using var alice = TasksClient.Create(address, alicesData, "alice", "team-1", inner => new LoseFirstResponse(lost) { InnerHandler = inner });
        for (var attempt = 0; attempt < 5 && (await alice.Intents.GetAsync(completion))!.State == IntentStates.Pending; attempt++)
        {
            try
            {
                await alice.SyncNowAsync();
            }
            catch (Exception error) when (error is SyncTransportException or HttpRequestException)
            {
                // The lost response; the next attempt resends.
            }
        }

        var executed = await alice.Intents.GetAsync(completion);
        Assert.Equal(1, lost.Count);
        Assert.Equal((IntentStates.Executed, "alice"), (executed!.State, executed.ExecutedBy));
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't1' AND Done = 1 AND Revision = 2"));
        Assert.Equal((true, 2L), ((await alice.Tasks.GetAsync("t1"))!.Done, (await alice.Tasks.GetAsync("t1"))!.Revision));

        // 3. An intent based on a revision the task no longer has is accepted but not executed; its code stays.
        var stale = new TaskIntent { Kind = IntentKinds.Rename, TaskId = "t1", TaskRevision = 1, Title = "Something else" };
        await alice.Intents.SaveAsync(stale);
        await alice.SyncNowAsync();
        await alice.SyncNowAsync();
        var rejected = await alice.Intents.GetAsync(stale.Id);

        Assert.Equal((IntentStates.Rejected, IntentCodes.TargetChanged), (rejected!.State, rejected.Code));
        Assert.Equal(SyncItemState.Synced, (await alice.Intents.GetItemStatusAsync(stale.Id))!.State);
        Assert.Equal("Write report", (await alice.Tasks.GetAsync("t1"))!.Title);

        // 4. Intents are immutable: changing one is refused with a stable code, and the server's copy is unchanged.
        rejected.TaskRevision = 2;
        await alice.Intents.SaveAsync(rejected);
        await alice.SyncNowAsync();
        var edit = await alice.Intents.GetItemStatusAsync(stale.Id);
        Assert.Equal((SyncItemState.Rejected, IntentCodes.Immutable), (edit!.State, edit.Detail));
        Assert.Equal("Write report", (await alice.Tasks.GetAsync("t1"))!.Title);

        // 5. An intent for a task that has not reached the server yet waits (retry-later), then executes.
        await using var bob = TasksClient.Create(address, Path.Combine(_data, "bob"), "bob", "team-1");
        var early = new TaskIntent { Kind = IntentKinds.Rename, TaskId = "t9", Title = "Plan offsite" };
        await bob.Intents.SaveAsync(early);
        await bob.SyncNowAsync();
        Assert.Equal(IntentStates.Pending, (await bob.Intents.GetAsync(early.Id))!.State);
        Assert.Equal(SyncItemState.Pending, (await bob.Intents.GetItemStatusAsync(early.Id))!.State);
        Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't9'"));

        await alice.Tasks.SaveAsync(new TaskDocument { Id = "t9", Title = "Offsite" });
        await alice.SyncNowAsync();
        await bob.SyncNowAsync();

        Assert.Equal((IntentStates.Executed, "bob"), ((await bob.Intents.GetAsync(early.Id))!.State, (await bob.Intents.GetAsync(early.Id))!.ExecutedBy));
        Assert.Equal("plan-offsite", (await bob.Tasks.GetAsync("t9"))!.Slug);
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM dbo.Tasks WHERE Id = 't9' AND Title = 'Plan offsite' AND Revision = 2 AND ChangedBy = 'bob'"));
    }

    [Fact(DisplayName = "F1 I01 I16: a 50 MB attachment survives a network drop at 60% and a restart in both directions; a corrupted download is never opened; reads follow task access")]
    public async Task AttachmentsSurviveInterruptions()
    {
        const int Size = 50 * 1024 * 1024;
        var content = new byte[Size];
        new Random(42).NextBytes(content);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var address = FreeAddress();
        await using var server = await StartServerAsync(address);
        await server.StartAsync();
        var faults = new Faults();
        Func<HttpMessageHandler, HttpMessageHandler> faulty = inner => new FaultyHandler(faults) { InnerHandler = inner };
        var alicesData = Path.Combine(_data, "alice");
        var objects = Path.Combine(_data, "server-blobs", "objects");

        // 1. The attachment is durable locally at once. The upload drops at 60%; the task is held back meanwhile, so the
        //    server never sees a reference to bytes it does not have.
        BlobReference blob;
        await using (var first = TasksClient.Create(address, alicesData, "alice", "team-1", faulty))
        {
            await first.Tasks.SaveAsync(new TaskDocument { Id = "t1", Title = "Contract" });
            blob = await first.AttachAsync("t1", new MemoryStream(content), "contract.bin", "application/octet-stream");
            await using (var local = first.OpenAttachment(blob))
            {
                Assert.Equal(Size, local!.Length);
            }

            faults.CutUploadAfter = Size * 6L / 10;
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => first.SyncNowAsync());
            Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM dbo.TaskAttachments WHERE TaskId = 't1'")); // the edit that adds it waits
            Assert.Equal(SyncItemState.Pending, (await first.Tasks.GetItemStatusAsync("t1"))!.State);
        }

        // 2. After a restart the upload resumes where the server stopped, not from zero. The response to the finishing
        //    request is lost; repeating it stores no second object. Then the task uploads.
        var uploadedBeforeRestart = faults.UploadBytes;
        await using var alice = TasksClient.Create(address, alicesData, "alice", "team-1", faulty);
        faults.LoseFinishResponse = true;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => alice.SyncNowAsync());
        await alice.SyncNowAsync();

        Assert.InRange(uploadedBeforeRestart, Size / 2, Size * 6L / 10);
        Assert.InRange(faults.UploadBytes, Size, Size + (4 * 1024 * 1024)); // at most one chunk sent twice
        Assert.Equal(SyncItemState.Synced, (await alice.Tasks.GetItemStatusAsync("t1"))!.State);
        Assert.Equal(1, await _database.ScalarAsync($"SELECT count(*) FROM dbo.TaskAttachments WHERE TaskId = 't1' AND Sha256 = '{sha256}'"));
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM dbo.TenantBlobs"));
        Assert.Single(Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories));

        // 3. Another member of the team receives the task. The bytes are not on the device yet: the task is readable and
        //    the attachment unavailable. The download drops at 60%; the partial file is never opened as complete.
        var bobsData = Path.Combine(_data, "bob");
        BlobReference received;
        await using (var first = TasksClient.Create(address, bobsData, "bob", "team-1", faulty))
        {
            await first.SyncNowAsync();
            received = (await first.Tasks.GetAsync("t1"))!.Attachments.Single();
            Assert.Equal(blob, received);
            Assert.Null(first.OpenAttachment(received));

            faults.CutDownloadAfter = Size * 6L / 10;
            await Assert.ThrowsAnyAsync<IOException>(() => first.DownloadAsync(received));
            Assert.Null(first.OpenAttachment(received));
        }

        // 4. The partial file is damaged while the app is not running. The resumed download completes, fails
        //    verification and is discarded; the next attempt (here: prefetching a pinned task) gets verified content.
        var partial = Directory.EnumerateFiles(Path.Combine(bobsData, "blobs-bob", "partial")).Single();
        var downloadedBeforeRestart = faults.DownloadBytes;
        await using (var file = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite))
        {
            Assert.InRange(file.Length, Size / 2, Size * 6L / 10);
            file.Position = 1000;
            file.WriteByte((byte)(content[1000] ^ 0xFF));
        }

        await using var bob = TasksClient.Create(address, bobsData, "bob", "team-1", faulty);
        await Assert.ThrowsAsync<BlobCorruptedException>(() => bob.DownloadAsync(received));
        Assert.InRange(faults.DownloadBytes - downloadedBeforeRestart, Size * 3L / 10, Size / 2); // resumed, not restarted
        Assert.Null(bob.OpenAttachment(received));
        Assert.False(File.Exists(partial));

        bob.Pin("t1");
        await bob.SyncNowAsync();
        await using (var opened = bob.OpenAttachment(received))
        {
            Assert.Equal(sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(opened!)));
        }

        // 5. Reads follow task access: another team cannot read the blob, and learns nothing from uploading the same
        //    bytes, which it must send in full (the object is still stored once).
        await using var eve = TasksClient.Create(address, Path.Combine(_data, "eve"), "eve", "team-2");
        using var http = new HttpClient { BaseAddress = address };
        var token = await (await http.PostAsJsonAsync("api/token", new TokenRequest("eve", "team-2"), TasksJson.Default.TokenRequest)).Content.ReadFromJsonAsync(TasksJson.Default.TokenResponse);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/blobs/{sha256}")).StatusCode);

        await eve.Tasks.SaveAsync(new TaskDocument { Id = "e1", Title = "Same file" });
        await eve.AttachAsync("e1", new MemoryStream(content), "copy.bin", "application/octet-stream");
        Assert.Equal(Size, await eve.UploadAttachmentsAsync());
        await eve.SyncNowAsync();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"api/blobs/{sha256}", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
        Assert.Single(Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories));
        Assert.Equal(2, await _database.ScalarAsync("SELECT count(*) FROM dbo.TenantBlobs"));

        // 6. Eviction removes unpinned, synced content, never what a pending task still has to upload.
        faults.Offline = true;
        await alice.Tasks.SaveAsync(new TaskDocument { Id = "t2", Title = "Draft" });
        var draft = await alice.AttachAsync("t2", new MemoryStream([1, 2, 3]), "draft.txt", "text/plain");
        await alice.EvictAsync(0);

        Assert.Null(alice.OpenAttachment(blob));
        await using (var kept = alice.OpenAttachment(draft))
        {
            Assert.NotNull(kept); // the pending task still has to upload it
        }

        faults.Offline = false;
        await alice.SyncNowAsync();
        Assert.Equal(SyncItemState.Synced, (await alice.Tasks.GetItemStatusAsync("t2"))!.State);
        Assert.Equal(Size, await alice.DownloadAsync(blob)); // evicted content comes back on demand
    }

    [Fact(DisplayName = "F2 I01 I16: an interrupted bundle update keeps the previous revision in use and reported, across a restart; completion switches revision at once")]
    public async Task BundleSwitchesAtomically()
    {
        var address = FreeAddress();
        await using var server = await StartServerAsync(address);
        await server.StartAsync();
        var big = new byte[20 * 1024 * 1024];
        new Random(7).NextBytes(big);

        // The back office uploads content and publishes revision 1.
        await using var carol = TasksClient.Create(address, Path.Combine(_data, "carol"), "carol", "team-1");
        using var office = await SignedInAsync(address, "carol", "team-1");
        var a1 = await carol.UploadContentAsync(new MemoryStream("v1"u8.ToArray()), "a.txt", "text/plain");
        var b = await carol.UploadContentAsync(new MemoryStream(new byte[2 * 1024 * 1024]), "b.bin", "application/octet-stream");
        Assert.Equal(HttpStatusCode.OK, (await office.PostAsJsonAsync("api/bundles", new PublishBundle("handbook", [a1, b]), TasksJson.Default.PublishBundle)).StatusCode);

        var faults = new Faults();
        var bobsData = Path.Combine(_data, "bob");
        await using (var first = TasksClient.Create(address, bobsData, "bob", "team-1", inner => new FaultyHandler(faults) { InnerHandler = inner }))
        {
            await first.SyncNowAsync();
            Assert.Equal(new BundleState("handbook", 1, 1, [], 0), await first.GetBundleStateAsync("handbook") with { Missing = [] });
            Assert.True((await first.GetBundleStateAsync("handbook")).IsCurrent);
            Assert.Equal("v1", await ReadItemAsync(first, first.GetActiveBundle("handbook")!, "a.txt"));

            // Revision 2 changes a.txt and adds a 20 MiB file; the device's update drops halfway through that file.
            var a2 = await carol.UploadContentAsync(new MemoryStream("v2"u8.ToArray()), "a.txt", "text/plain");
            var c = await carol.UploadContentAsync(new MemoryStream(big), "c.bin", "application/octet-stream");
            Assert.Equal(HttpStatusCode.OK, (await office.PostAsJsonAsync("api/bundles", new PublishBundle("handbook", [a2, b, c]), TasksJson.Default.PublishBundle)).StatusCode);
            faults.CutDownloadAfter = faults.DownloadBytes + (10 * 1024 * 1024);
            await Assert.ThrowsAnyAsync<IOException>(() => first.SyncNowAsync());

            var interrupted = await first.GetBundleStateAsync("handbook");
            Assert.Equal((1L, 2L, false, true), (interrupted.ActiveRevision, interrupted.LatestRevision, interrupted.IsCurrent, interrupted.IsAvailable));
            Assert.Equal(["c.bin"], interrupted.Missing);
            Assert.InRange(interrupted.BytesRemaining, 1, (big.Length / 2) + 1024); // about half (a.txt v2 took 2 bytes of the budget)
            Assert.Equal("v1", await ReadItemAsync(first, first.GetActiveBundle("handbook")!, "a.txt")); // a.txt v2 is on the device, unused
        }

        // After a restart, the device still uses and reports revision 1; the next update completes and switches.
        await using var bob = TasksClient.Create(address, bobsData, "bob", "team-1", inner => new FaultyHandler(faults) { InnerHandler = inner });
        var restarted = await bob.GetBundleStateAsync("handbook");
        Assert.Equal((1L, 2L), (restarted.ActiveRevision, restarted.LatestRevision));
        Assert.Equal("v1", await ReadItemAsync(bob, bob.GetActiveBundle("handbook")!, "a.txt"));

        var downloadedBefore = faults.DownloadBytes;
        await bob.SyncNowAsync();
        var complete = await bob.GetBundleStateAsync("handbook");
        var active = bob.GetActiveBundle("handbook")!;

        Assert.Equal((2L, 2L, true, 0L), (complete.ActiveRevision, complete.LatestRevision, complete.IsCurrent, complete.BytesRemaining));
        Assert.InRange(faults.DownloadBytes - downloadedBefore, 1, (big.Length / 2) + 1024); // resumed: the other half
        Assert.Equal(2, active.Revision);
        Assert.Equal("v2", await ReadItemAsync(bob, active, "a.txt"));
        await using (var item = bob.OpenAttachment(active.Items.Single(i => i.FileName == "c.bin")))
        {
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(big)), Convert.ToHexStringLower(await SHA256.HashDataAsync(item!)));
        }

        // Replicas cannot write bundles, and another team cannot read their content.
        await Assert.ThrowsAsync<SyncReadOnlyException>(() => bob.Bundles.SaveAsync(new BundleManifest { Id = "forged" }));
        using var eve = await SignedInAsync(address, "eve", "team-2");
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"api/blobs/{active.Items.Single(i => i.FileName == "c.bin").Sha256}")).StatusCode);
    }

    private static async Task<HttpClient> SignedInAsync(Uri address, string user, string tenant)
    {
        var http = new HttpClient { BaseAddress = address };
        var token = await (await http.PostAsJsonAsync("api/token", new TokenRequest(user, tenant), TasksJson.Default.TokenRequest)).Content.ReadFromJsonAsync(TasksJson.Default.TokenResponse);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token);
        return http;
    }

    private static async Task<string> ReadItemAsync(TasksClient client, BundleManifest manifest, string name)
    {
        await using var stream = client.OpenAttachment(manifest.Items.Single(i => i.FileName == name))!;
        return await new StreamReader(stream).ReadToEndAsync();
    }

    [S3Fact(DisplayName = "F1 I01: with an S3-compatible store, attachments land in the bucket once and downloads resume from presigned URLs")]
    public async Task AttachmentsInObjectStorage()
    {
        var serviceUrl = Environment.GetEnvironmentVariable("BSYNC_S3")!;
        var bucket = $"bsync-{Guid.NewGuid():N}";
        using var s3 = new Amazon.S3.AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials("test", "test"),
            new Amazon.S3.AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        await s3.PutBucketAsync(bucket);
        var content = new byte[6 * 1024 * 1024];
        new Random(11).NextBytes(content);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var address = FreeAddress();
        await using var server = await StartServerAsync(address, new Dictionary<string, string>
        {
            ["Tasks:S3:ServiceUrl"] = serviceUrl,
            ["Tasks:S3:Bucket"] = bucket,
            ["Tasks:S3:AccessKey"] = "test",
            ["Tasks:S3:SecretKey"] = "test",
        });
        await server.StartAsync();

        await using var alice = TasksClient.Create(address, Path.Combine(_data, "alice"), "alice", "team-1");
        await alice.Tasks.SaveAsync(new TaskDocument { Id = "t1", Title = "Report" });
        var blob = await alice.AttachAsync("t1", new MemoryStream(content), "report.bin", "application/octet-stream");
        await alice.SyncNowAsync();

        var stored = await s3.GetObjectMetadataAsync(bucket, $"objects/{sha256[..2]}/{sha256}");
        Assert.Equal(content.Length, stored.ContentLength);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_data, "server-blobs"), "*", SearchOption.AllDirectories)); // nothing kept on the server

        var faults = new Faults { CutDownloadAfter = content.Length / 2 };
        await using var bob = TasksClient.Create(address, Path.Combine(_data, "bob"), "bob", "team-1", inner => new FaultyHandler(faults) { InnerHandler = inner });
        await bob.SyncNowAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => bob.DownloadAsync(blob));
        var resumed = await bob.DownloadAsync(blob);

        Assert.InRange(resumed, 1, content.Length / 2); // the rest only, by a range request to the presigned URL
        Assert.Equal(2, faults.ServedElsewhere); // both downloads were served by the object store, not the application server
        await using (var opened = bob.OpenAttachment(blob))
        {
            Assert.Equal(sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(opened!)));
        }

        using var eve = await SignedInAsync(address, "eve", "team-2");
        Assert.Equal(HttpStatusCode.NotFound, (await eve.GetAsync($"api/blobs/{sha256}")).StatusCode); // no URL for others
    }

    /// <summary>Faults injected into one client's HTTP traffic, and what crossed the network.</summary>
    private sealed class Faults
    {
        public long CutUploadAfter = long.MaxValue;

        public long CutDownloadAfter = long.MaxValue;

        public volatile bool LoseFinishResponse;

        public volatile bool Offline;

        public long UploadBytes;

        public long DownloadBytes;

        public int ServedElsewhere;
    }

    /// <summary>Drops connections part-way through blob transfers, loses a response, or fails everything (offline).</summary>
    private sealed class FaultyHandler(Faults faults) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (faults.Offline)
            {
                throw new HttpRequestException("No network (test).");
            }

            var path = request.RequestUri!.AbsolutePath;
            var port = request.RequestUri.Port; // following a redirect changes the request's URI in place
            if (request.Method == HttpMethod.Put && path.Contains("/uploads/", StringComparison.Ordinal) && request.Content is { } body)
            {
                var bytes = await body.ReadAsByteArrayAsync(cancellationToken);
                var before = Interlocked.Add(ref faults.UploadBytes, bytes.Length) - bytes.Length;
                if (before + bytes.Length > Interlocked.Read(ref faults.CutUploadAfter))
                {
                    // The connection drops after part of this chunk was sent.
                    var kept = (int)Math.Max(0, faults.CutUploadAfter - before);
                    Interlocked.Exchange(ref faults.CutUploadAfter, long.MaxValue);
                    Interlocked.Add(ref faults.UploadBytes, kept - bytes.Length);
                    request.Content = new DroppedContent(bytes, kept);
                }
                else
                {
                    request.Content = new ByteArrayContent(bytes);
                }
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (faults.LoseFinishResponse && path.EndsWith("/uploads/finish", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                faults.LoseFinishResponse = false;
                response.Dispose();
                throw new HttpRequestException("The response was lost (test).");
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/blobs/", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                if (response.RequestMessage?.RequestUri is { } final && final.Port != port)
                {
                    Interlocked.Increment(ref faults.ServedElsewhere); // redirected to the object store
                }

                response.Content = new StreamContent(new DroppingStream(await response.Content.ReadAsStreamAsync(cancellationToken), faults));
            }

            return response;
        }
    }

    /// <summary>Request content whose connection drops after <c>kept</c> bytes.</summary>
    private sealed class DroppedContent(byte[] bytes, int kept) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, kept));
            await stream.FlushAsync();
            throw new IOException("The connection dropped (test).");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }

    /// <summary>A response body that counts what arrives and drops at the configured point.</summary>
    private sealed class DroppingStream(Stream inner, Faults faults) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var cut = Interlocked.Read(ref faults.CutDownloadAfter);
            var room = cut - Interlocked.Read(ref faults.DownloadBytes);
            if (room <= 0)
            {
                Interlocked.Exchange(ref faults.CutDownloadAfter, long.MaxValue);
                throw new IOException("The connection dropped (test).");
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, room)], cancellationToken);
            Interlocked.Add(ref faults.DownloadBytes, read);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Which responses were lost; shared by the handlers of one client's transports.</summary>
    private sealed class LostResponses(string path)
    {
        public string Path { get; } = path;

        public int Count;
    }

    /// <summary>Sends the first matching request, then loses its response, as a dropped connection would.</summary>
    private sealed class LoseFirstResponse(LostResponses lost) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith(lost.Path, StringComparison.Ordinal)
                && response.IsSuccessStatusCode
                && Interlocked.CompareExchange(ref lost.Count, 1, 0) == 0)
            {
                response.Dispose();
                throw new HttpRequestException("The connection was closed before the response arrived (test).");
            }

            return response;
        }
    }

    [Fact(DisplayName = "B5 I07: sync and the API refuse callers without a bearer token")]
    public async Task RequiresBearerToken()
    {
        var address = FreeAddress();
        await using var server = await StartServerAsync(address);
        await server.StartAsync();
        using var http = new HttpClient { BaseAddress = address };

        var pull = await http.PostAsync($"sync/collections/{TasksJson.Collection}/pull", new StringContent("{}"));
        var api = await http.PostAsJsonAsync("api/tasks", new NewTask("x", "y"), TasksJson.Default.NewTask);

        Assert.Equal(HttpStatusCode.Unauthorized, pull.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }
}
