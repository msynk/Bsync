using System.Text.Json;
using Bsync.Storage;
using Bsync.Testing;
using Microsoft.Playwright;
using Xunit;

namespace Bsync.Tests.Browser;

/// <summary>
/// The IndexedDB store and the WebAssembly + HTTP slice in real browser engines (Playwright's Chromium,
/// Firefox and WebKit builds; native Safari and mobile browsers are not covered).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class IndexedDbBrowserTests(BrowserFixture fixture)
{
    public static TheoryData<string> Browsers() => new() { "chromium", "firefox", "webkit" };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<(IBrowserContext Context, HarnessPage Page)> NewDeviceAsync(string browser, BrowserNewContextOptions? options = null)
    {
        var context = await (await fixture.BrowserAsync(browser)).NewContextAsync(options);
        return (context, await HarnessPage.OpenAsync(context, fixture.BaseAddress));
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Theory(DisplayName = "Records the browser engine version under test")]
    [MemberData(nameof(Browsers))]
    public async Task EngineVersion(string browser)
    {
        var version = (await fixture.BrowserAsync(browser)).Version;
        Assert.False(string.IsNullOrEmpty(version));
        Console.WriteLine($"BROWSER-VERSION {browser} {version}");
    }

    [Theory(DisplayName = "T41-T45 I01-I03 I12: IndexedDB passes the shared store conformance cases")]
    [MemberData(nameof(Browsers))]
    public async Task StoreConformance(string browser)
    {
        var (context, page) = await NewDeviceAsync(browser);
        await using var _ = context;

        var json = await page.CallAsync("RunStoreConformance", Unique("conformance"), false);
        var results = JsonSerializer.Deserialize<List<CaseResult>>(json, Web)!;

        Assert.Equal(LocalStoreConformance.Cases.Count + LocalStoreIndexConformance.Cases.Count, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.Name}: {r.Error}"));
    }

    [Theory(DisplayName = "ADR-016: an encrypted IndexedDB replica passes the shared store and index conformance cases")]
    [MemberData(nameof(Browsers))]
    public async Task EncryptedStoreConformance(string browser)
    {
        var (context, page) = await NewDeviceAsync(browser);
        await using var _ = context;

        var json = await page.CallAsync("RunStoreConformance", Unique("encrypted"), true);
        var results = JsonSerializer.Deserialize<List<CaseResult>>(json, Web)!;

        Assert.Equal(LocalStoreConformance.Cases.Count + LocalStoreIndexConformance.Cases.Count, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.Name}: {r.Error}"));
    }

    [Theory(DisplayName = "ADR-016: IndexedDB stores documents sealed (AES-GCM); a missing or wrong key is refused, never treated as an empty replica")]
    [MemberData(nameof(Browsers))]
    public async Task EncryptionAtRest(string browser)
    {
        var (context, page) = await NewDeviceAsync(browser);
        await using var _ = context;
        var database = Unique("sealed");

        var steps = (await page.CallAsync("RunEncryptionChecks", database)).Split(" ;; ");
        var raw = await page.Page.EvaluateAsync<string>(
            """
            name => new Promise((resolve, reject) => {
              const open = indexedDB.open(name);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const all = open.result.transaction("records").objectStore("records").getAll();
                all.onsuccess = () => { open.result.close(); resolve(JSON.stringify(all.result)); };
              };
            })
            """,
            database);

        Assert.Equal(["none:key", "wrong:key", "right:secret-title-0123456789"], steps);
        Assert.Contains("enc1:", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-title", raw, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "F1: the browser blob store reads back imported content, refuses a corrupted transfer, continues a transfer after a reload, and wipes")]
    [MemberData(nameof(Browsers))]
    public async Task BrowserBlobStore(string browser)
    {
        var (context, page) = await NewDeviceAsync(browser);
        await using var _ = context;
        var name = Unique("blobs");

        var checks = (await page.CallAsync("RunBlobChecks", name)).Split(" ;; ");
        var sha = await page.CallAsync("BlobFirstHalf", name + "-resume");
        await page.Page.ReloadAsync();
        await page.Page.WaitForFunctionAsync("() => window.bsyncReady === true", null, new PageWaitForFunctionOptions { Timeout = 60_000 });
        var resumed = (await page.CallAsync("BlobSecondHalf", name + "-resume", sha)).Split(" ;; ");

        Assert.Equal(["import:True", "corrupt:False|True|0", "list:True", "wiped:0"], checks);
        Assert.Equal(["kept:1048576", "completed:True", $"size:{2 * 1024 * 1024}"], resumed);
    }

    [Theory(DisplayName = "ADR-018: IndexedDB keeps and uses index keys, falls back when another writer makes them unusable, and rebuilds on the next open")]
    [MemberData(nameof(Browsers))]
    public async Task IndexLifecycle(string browser)
    {
        var (context, page) = await NewDeviceAsync(browser);
        await using var _ = context;
        var signature = LocalStoreIndexing.Signature(LocalStoreIndexConformance.Indexes);

        var steps = (await page.CallAsync("RunIndexLifecycle", Unique("index"))).Split(" ;; ");

        Assert.Equal(
            [
                $"rebuilt:{signature}|b,a",
                $"maintained:{signature}|c,b,a|3",
                "invalidated:!|c,d,b,a",
                $"reopened:{signature}|c,d,b,a",
            ],
            steps);
    }

    [Theory(DisplayName = "T51 T52 I01 I13 I20: offline edits survive a reload, upload on reconnect and reach another device")]
    [MemberData(nameof(Browsers))]
    public async Task OfflineEditsSurviveReloadAndConverge(string browser)
    {
        fixture.Authority.Reset();
        var database = Unique("account");
        var (laptop, page) = await NewDeviceAsync(browser);
        await using var _ = laptop;
        Assert.Equal("ok", await page.CallAsync("OpenReplica", database, "laptop"));
        await page.SyncAsync();

        await laptop.SetOfflineAsync(true);
        Assert.Equal("1", await page.CallAsync("Write", "n1", "written offline"));
        Assert.Equal("1", await page.CallAsync("Write", "n2", "also offline"));
        Assert.Equal("error:transport:unavailable", await page.CallAsync("Sync"));

        // Close the tab; reopen once the network is back (the app itself is not cached offline here).
        await page.Page.CloseAsync();
        await laptop.SetOfflineAsync(false);
        page = await HarnessPage.OpenAsync(laptop, fixture.BaseAddress);
        Assert.Equal("ok", await page.CallAsync("OpenReplica", database, "laptop"));
        var reopened = await page.QueryAsync();
        Assert.Equal(["n1", "n2"], reopened.Select(d => d.Id));
        Assert.All(reopened, d => Assert.True(d.Dirty));

        var pushed = await page.SyncAsync();
        Assert.Equal(2, pushed.Pushed);
        Assert.True(pushed.Complete);

        var (phone, other) = await NewDeviceAsync(browser);
        await using var __ = phone;
        Assert.Equal("ok", await other.CallAsync("OpenReplica", database, "phone"));
        await other.SyncAsync();
        Assert.Equal("1", await other.CallAsync("Write", "n1", "edited on phone"));
        Assert.Equal("2", await page.CallAsync("Delete", "n2"));

        for (var i = 0; i < 2; i++)
        {
            await page.SyncAsync();
            await other.SyncAsync();
        }

        var laptopView = await page.QueryAsync();
        Assert.Equal(laptopView, await other.QueryAsync());
        Assert.Equal("edited on phone", laptopView.Single(d => d.Id == "n1").Title);
        Assert.True(laptopView.Single(d => d.Id == "n2").Deleted);
        Assert.All(laptopView, d => Assert.False(d.Dirty));
    }

    [Theory(DisplayName = "T41 I02: an acknowledgement in one tab never cleans a newer edit made in another tab")]
    [MemberData(nameof(Browsers))]
    public async Task TabsDoNotAcknowledgeEachOthersEdits(string browser)
    {
        fixture.Authority.Reset();
        var database = Unique("tabs");
        var (context, first) = await NewDeviceAsync(browser);
        await using var _ = context;
        var second = await HarnessPage.OpenAsync(context, fixture.BaseAddress);
        Assert.Equal("ok", await first.CallAsync("OpenReplica", database, "tab-1"));
        Assert.Equal("ok", await second.CallAsync("OpenReplica", database, "tab-2"));

        await first.CallAsync("Write", "t1", "v1");
        fixture.Authority.PushDelay = TimeSpan.FromSeconds(2);
        try
        {
            var inFlight = first.SyncAsync();
            await Task.Delay(700);
            Assert.Equal("2", await second.CallAsync("Write", "t1", "v2"));
            await inFlight;
        }
        finally
        {
            fixture.Authority.PushDelay = TimeSpan.Zero;
        }

        // Tab 1's acknowledgement of v1 must neither overwrite v2 nor mark it clean; its sync run then
        // pushes v2 from the shared store (the queue is drained in the same run).
        Assert.Equal("v2", (await second.QueryAsync()).Single().Title);
        await second.SyncAsync();
        Assert.Equal("v2", fixture.Authority.Server.Snapshot().Single().Title);
        Assert.Equal(2, fixture.Authority.Server.ReceiptCount);
        var final = (await first.QueryAsync()).Single();
        Assert.Equal("v2", final.Title);
        Assert.False(final.Dirty);
    }

    [Theory(DisplayName = "T41 I02: concurrent writes from two tabs to one record never lose or duplicate a revision")]
    [MemberData(nameof(Browsers))]
    public async Task ConcurrentTabsSerializeWrites(string browser)
    {
        var database = Unique("contention");
        var (context, first) = await NewDeviceAsync(browser);
        await using var _ = context;
        var second = await HarnessPage.OpenAsync(context, fixture.BaseAddress);
        Assert.Equal("ok", await first.CallAsync("OpenReplica", database, "tab-1"));
        Assert.Equal("ok", await second.CallAsync("OpenReplica", database, "tab-2"));

        async Task<List<string>> WriteMany(HarnessPage page, string tab)
        {
            var revisions = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                revisions.Add(await page.CallAsync("Write", "shared", $"{tab}-{i}"));
            }

            return revisions;
        }

        var results = await Task.WhenAll(WriteMany(first, "a"), WriteMany(second, "b"));
        var revisions = results.SelectMany(r => r).Select(long.Parse).Order().ToList();

        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), revisions);
    }

    [Theory(DisplayName = "T42: the replication lease is exclusive and is released when the owning tab closes")]
    [MemberData(nameof(Browsers))]
    public async Task LeaseIsExclusive(string browser)
    {
        var name = Unique("lease");
        var (context, owner) = await NewDeviceAsync(browser);
        await using var _ = context;
        var follower = await HarnessPage.OpenAsync(context, fixture.BaseAddress);

        Assert.Equal("acquired", await owner.CallAsync("TryLease", name));
        Assert.Equal("busy", await follower.CallAsync("TryLease", name));

        await owner.Page.CloseAsync();
        var acquired = false;
        for (var attempt = 0; attempt < 50 && !acquired; attempt++)
        {
            acquired = await follower.CallAsync("TryLease", name) == "acquired";
            if (!acquired)
            {
                await Task.Delay(100);
            }
        }

        Assert.True(acquired);
    }

    [Theory(DisplayName = "T44 I01: unavailable IndexedDB is reported, not treated as durable success")]
    [MemberData(nameof(Browsers))]
    public async Task UnavailableStorageIsReported(string browser)
    {
        var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await using var _ = context;
        await context.AddInitScriptAsync("Object.defineProperty(window, 'indexedDB', { value: undefined, configurable: true });");
        var page = await HarnessPage.OpenAsync(context, fixture.BaseAddress);

        Assert.Equal("error:store:unavailable", await page.CallAsync("OpenReplica", Unique("no-idb"), "n"));
    }

    [Theory(DisplayName = "T43 I17: a schema upgrade from another tab is not blocked; the old tab fails explicitly")]
    [MemberData(nameof(Browsers))]
    public async Task UpgradeFromAnotherTab(string browser)
    {
        var database = Unique("upgrade");
        var (context, oldTab) = await NewDeviceAsync(browser);
        await using var _ = context;
        var newTab = await HarnessPage.OpenAsync(context, fixture.BaseAddress);
        Assert.Equal("ok", await oldTab.CallAsync("OpenReplica", database, "old"));

        // Any version above the current schema (4) stands in for a newer application.
        var upgrade = await newTab.Page.EvaluateAsync<string>(
            """
            name => new Promise(resolve => {
              const request = indexedDB.open(name, 1000);
              request.onsuccess = () => { request.result.close(); resolve("ok"); };
              request.onerror = () => resolve("error:" + request.error.name);
              request.onblocked = () => resolve("blocked");
            })
            """,
            database);

        Assert.Equal("ok", upgrade);
        Assert.Equal("error:store:outdated", await oldTab.CallAsync("Write", "x", "after upgrade"));
        Assert.Equal("error:store:outdated", await oldTab.CallAsync("OpenReplica", database, "old"));
    }

    [Theory(DisplayName = "I01 I17: a schema 1 browser database with pending work upgrades to the current schema (4) and still uploads it once")]
    [MemberData(nameof(Browsers))]
    public async Task SchemaOneUpgradeKeepsPendingWork(string browser)
    {
        fixture.Authority.Reset();
        var database = Unique("migrate");
        var (context, page) = await NewDeviceAsync(browser);
        await using var _ = context;
        Assert.Equal("ok", await page.CallAsync("OpenReplica", database, "m"));
        Assert.Equal("1", await page.CallAsync("Write", "m1", "written by schema 1"));
        var identity = await page.CallAsync("Identity");
        Assert.Equal("ok", await page.CallAsync("CloseReplica"));

        // Rebuild the same data as a schema 1 database: the version 1 layout, without the conflict index or fields.
        var rebuilt = await page.Page.EvaluateAsync<string>(
            """
            async name => {
              const done = r => new Promise((ok, fail) => { r.onsuccess = () => ok(r.result); r.onerror = () => fail(r.error); });
              const current = await done(indexedDB.open(name));
              const read = current.transaction(["records", "meta"]);
              const records = await done(read.objectStore("records").getAll());
              const meta = await done(read.objectStore("meta").getAll());
              current.close();
              await done(indexedDB.deleteDatabase(name));
              const open = indexedDB.open(name, 1);
              open.onupgradeneeded = () => {
                const db = open.result;
                const store = db.createObjectStore("records", { keyPath: ["collection", "id"] });
                for (const index of ["pending", "dirty", "stale", "visible", "live"]) store.createIndex(index, index + "Key");
                db.createObjectStore("meta", { keyPath: ["collection", "key"] });
              };
              const v1 = await done(open);
              const write = v1.transaction(["records", "meta"], "readwrite");
              for (const record of records) {
                for (const key of Object.keys(record)) if (key.startsWith("conflict")) delete record[key];
                write.objectStore("records").put(record);
              }
              for (const row of meta) write.objectStore("meta").put(row);
              await new Promise((ok, fail) => { write.oncomplete = ok; write.onerror = () => fail(write.error); });
              const version = v1.version;
              v1.close();
              return `${version}:${records.length}`;
            }
            """,
            database);
        Assert.Equal("1:1", rebuilt);

        Assert.Equal("ok", await page.CallAsync("OpenReplica", database, "m"));
        Assert.Equal(identity, await page.CallAsync("Identity"));
        Assert.True((await page.QueryAsync()).Single().Dirty);
        Assert.Equal(1, (await page.SyncAsync()).Pushed);
        Assert.Equal("written by schema 1", fixture.Authority.Server.Snapshot().Single().Title);
        Assert.Equal(1, fixture.Authority.Server.ReceiptCount);
        Assert.Equal(4, await page.Page.EvaluateAsync<int>(
            "name => new Promise(ok => { const r = indexedDB.open(name); r.onsuccess = () => { const v = r.result.version; r.result.close(); ok(v); }; })",
            database));
    }

    [Theory(DisplayName = "T45 I10: a deleted browser database is detected (new replica id) and repopulated from the server")]
    [MemberData(nameof(Browsers))]
    public async Task DeletedDatabaseIsRepopulated(string browser)
    {
        fixture.Authority.Reset();
        var database = Unique("evicted");
        var (context, page) = await NewDeviceAsync(browser);
        await using var _ = context;
        Assert.Equal("ok", await page.CallAsync("OpenReplica", database, "d"));
        await page.CallAsync("Write", "kept", "on the server");
        await page.SyncAsync();
        var before = await page.CallAsync("Identity");

        Assert.Equal("ok", await page.CallAsync("CloseReplica"));
        Assert.Equal("ok", await page.CallAsync("DeleteDatabase", database));
        Assert.Equal("ok", await page.CallAsync("OpenReplica", database, "d"));

        Assert.Empty(await page.QueryAsync());
        Assert.NotEqual(before, await page.CallAsync("Identity"));
        await page.SyncAsync();
        Assert.Equal("on the server", (await page.QueryAsync()).Single().Title);
    }

    [Theory(DisplayName = "T35 I10 I14: after a server restore the browser replica resets, keeps pending edits and hides lost records")]
    [MemberData(nameof(Browsers))]
    public async Task ResetAfterRestore(string browser)
    {
        fixture.Authority.Reset();
        var (context, page) = await NewDeviceAsync(browser);
        await using var _ = context;
        Assert.Equal("ok", await page.CallAsync("OpenReplica", Unique("reset"), "r"));
        await page.CallAsync("Write", "a", "before backup");
        await page.SyncAsync();
        var backup = fixture.Authority.Server.CreateBackup();
        await page.CallAsync("Write", "b", "lost by the restore");
        await page.SyncAsync();
        await page.CallAsync("Write", "c", "pending across the restore");

        fixture.Authority.Restore(backup);
        var result = await page.SyncAsync();

        Assert.True(result.Reset);
        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Pushed);
        var view = await page.QueryAsync();
        Assert.Equal(["a", "c"], view.Select(d => d.Id));
        Assert.All(view, d => Assert.False(d.Dirty));
    }

    public sealed record CaseResult(string Name, bool Passed, string? Error);

    public sealed record SyncSummary(int Pulled, int Pushed, int Conflicts, bool Complete, bool Reset, int Missing);

    public sealed record DocumentView(string Id, string Title, bool Deleted, bool Dirty);

    /// <summary>A page running the harness, with typed wrappers over its [JSInvokable] methods.</summary>
    public sealed class HarnessPage(IPage page)
    {
        public IPage Page { get; } = page;

        public static async Task<HarnessPage> OpenAsync(IBrowserContext context, Uri baseAddress)
        {
            var page = await context.NewPageAsync();
            var log = new List<string>();
            page.Console += (_, message) => log.Add($"console.{message.Type}: {message.Text}");
            page.PageError += (_, error) => log.Add($"pageerror: {error}");
            page.RequestFailed += (_, request) => log.Add($"requestfailed: {request.Url} {request.Failure}");
            await page.GotoAsync(baseAddress.ToString());
            try
            {
                await page.WaitForFunctionAsync("() => window.bsyncReady === true", null, new PageWaitForFunctionOptions { Timeout = 60_000 });
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"The harness did not start. Browser output:\n{string.Join('\n', log.TakeLast(30))}", error);
            }

            return new HarnessPage(page);
        }

        public Task<string> CallAsync(string method, params object[] args) =>
            Page.EvaluateAsync<string>(
                "([method, args]) => DotNet.invokeMethodAsync('Bsync.Tests.BrowserHost', method, ...args)",
                new object[] { method, args });

        public async Task<SyncSummary> SyncAsync()
        {
            var json = await CallAsync("Sync");
            Assert.StartsWith("{", json);
            return JsonSerializer.Deserialize<SyncSummary>(json, Web)!;
        }

        public async Task<List<DocumentView>> QueryAsync()
        {
            var json = await CallAsync("Query");
            Assert.StartsWith("[", json);
            return JsonSerializer.Deserialize<List<DocumentView>>(json, Web)!;
        }
    }
}
