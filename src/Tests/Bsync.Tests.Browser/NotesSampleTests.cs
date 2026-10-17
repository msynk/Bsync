using Microsoft.Playwright;
using Xunit;

namespace Bsync.Tests.Browser;

/// <summary>
/// The published notes PWA sample (server process + WebAssembly client with a service worker), driven like a
/// user: offline reload, a service-worker update, a second device and a server restart.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class NotesSampleTests(BrowserFixture fixture) : IAsyncLifetime
{
    private readonly SampleServer _server = new("notes-sample", "Bsync.Samples.Notes.Server.dll");

    private string PublishDir => _server.PublishDir;

    private Uri Address => _server.Address;

    public static TheoryData<string> Browsers() => new() { "chromium", "firefox", "webkit" };

    // Playwright's WebKit build on Windows fails with "WebKit encountered an internal error" when a page is
    // reloaded through a service worker under emulated offline mode (seen with Playwright 1.63). The offline
    // app shell is therefore unverified in WebKit; docs/support-matrix.md says so.
    public static TheoryData<string> OfflineReloadBrowsers() => new() { "chromium", "firefox" };

    public Task InitializeAsync() => _server.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task StartServerAsync() => _server.StartAsync();

    private Task StopServerAsync() => _server.StopAsync();

    private async Task<IPage> OpenAppAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(Address.ToString());
        await Expect(page.GetByTestId("status")).Not.ToHaveTextAsync("Starting", new() { Timeout = 60_000 });
        return page;
    }

    /// <summary>Loads the app until its service worker controls the page (the first load only installs it).</summary>
    private async Task<IPage> OpenControlledAppAsync(IBrowserContext context)
    {
        var page = await OpenAppAsync(context);
        await page.EvaluateAsync("() => navigator.serviceWorker.ready");
        await page.ReloadAsync();
        await page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null", null, new() { Timeout = 30_000 });
        await Expect(page.GetByTestId("status")).Not.ToHaveTextAsync("Starting", new() { Timeout = 60_000 });
        return page;
    }

    private static async Task AddNoteAsync(IPage page, string text)
    {
        await page.GetByTestId("new-text").FillAsync(text);
        await page.GetByTestId("add").ClickAsync();
        await Expect(page.GetByTestId("note-text").Filter(new() { HasText = text })).ToBeVisibleAsync();
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);

    [Theory]
    [MemberData(nameof(OfflineReloadBrowsers))]
    public async Task ContentLibraryKeepsPriorRevisionAcrossOfflineInterruptedUpdate(string browser)
    {
        await using var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        var page = await OpenControlledAppAsync(context);
        await page.GetByTestId("library-one").ClickAsync();
        await Expect(page.GetByTestId("library-status")).ToContainTextAsync("Active: 1; latest: 1; ReadyFreshnessUnknown");
        await page.GetByTestId("library-interrupt").ClickAsync();
        await Expect(page.GetByTestId("library-status")).ToContainTextAsync("Active: 1; latest: 2; ReadyKnownOutdated");
        await context.SetOfflineAsync(true);
        await page.ReloadAsync();
        await Expect(page.GetByTestId("library-status")).ToContainTextAsync("Active: 1; latest: 2; ReadyKnownOutdated", new() { Timeout = 60000 });
        await page.GetByTestId("library-read").ClickAsync();
        await Expect(page.GetByTestId("library-text")).ToContainTextAsync("Revision 1:");
        await page.GetByTestId("library-two").ClickAsync();
        await Expect(page.GetByTestId("library-status")).ToContainTextAsync("Active: 2; latest: 2; ReadyFreshnessUnknown");
        await page.GetByTestId("library-read").ClickAsync();
        await Expect(page.GetByTestId("library-text")).ToContainTextAsync("Revision 2:");
        await page.ReloadAsync();
        await page.GetByTestId("library-read").ClickAsync();
        await Expect(page.GetByTestId("library-text")).ToContainTextAsync("Revision 2:");
    }

    [Theory]
    [MemberData(nameof(Browsers))]
    public async Task ServiceWorkerBypassesNonManifestApiBlobAndHintRequests(string browser)
    {
        await using var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        var page = await OpenControlledAppAsync(context);
        // Even an old/accidentally seeded shell cache must not supply data/API responses.
        var outcomes = await page.EvaluateAsync<string[]>("""
            async () => {
                const name = (await caches.keys()).find(k => k.startsWith('offline-cache-'));
                const cache = await caches.open(name);
                const paths = ['/api/private-probe', '/sync/blobs/private-probe', '/sync/private-probe/hints'];
                for (const path of paths) await cache.put(path, new Response('stale private data', { status: 200 }));
                return await Promise.all(paths.map(async path => (await fetch(path, { cache: 'no-store' })).text()));
            }
            """);
        // The sample server's SPA fallback may answer 200 HTML; the worker must never return the seeded data.
        Assert.All(outcomes, body => Assert.DoesNotContain("stale private data", body));
        // Remove the actual server rather than depending on WebKit's unsupported offline/service-worker emulation.
        await StopServerAsync();
        var offline = await page.EvaluateAsync<bool[]>("""
            async () => await Promise.all(['/api/private-probe', '/sync/blobs/private-probe', '/sync/private-probe/hints']
                .map(async path => {
                    const stop = new AbortController();
                    const deadline = setTimeout(() => stop.abort(), 2000);
                    try { await fetch(path, { cache: 'no-store', signal: stop.signal }); return true; }
                    catch { return false; }
                    finally { clearTimeout(deadline); }
                }))
            """);
        Assert.All(offline, responded => Assert.False(responded));
    }

    [Theory]
    [MemberData(nameof(OfflineReloadBrowsers))]
    public async Task RecoveryKeyUnlocksOfflineAfterReloadAndWrongKeyRetainsNotebook(string browser)
    {
        await using var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        var page = await OpenControlledAppAsync(context);
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await page.GetByText("Offline unlock example", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("Recovery key").FillAsync(key);
        await page.GetByRole(AriaRole.Button, new() { Name = "Unlock locally" }).ClickAsync();
        await Expect(page.GetByTestId("unlock-status")).ToContainTextAsync("Unlocked locally");
        await page.GetByLabel("Local note", new() { Exact = true }).FillAsync("private offline note");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save on this device" }).ClickAsync();
        await Expect(page.GetByTestId("unlock-status")).ToContainTextAsync("Saved locally");
        await page.GetByRole(AriaRole.Button, new() { Name = "Lock", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("unlock-status")).ToHaveTextAsync("Locked");
        await context.SetOfflineAsync(true);
        await page.ReloadAsync();
        await page.GetByText("Offline unlock example", new() { Exact = true }).ClickAsync(new() { Timeout = 60_000 });
        await page.GetByLabel("Recovery key").FillAsync(Convert.ToBase64String(new byte[32]));
        await page.GetByRole(AriaRole.Button, new() { Name = "Unlock locally" }).ClickAsync();
        await Expect(page.GetByTestId("unlock-status")).ToContainTextAsync("stored data was retained");
        await page.GetByLabel("Recovery key").FillAsync(key);
        await page.GetByRole(AriaRole.Button, new() { Name = "Unlock locally" }).ClickAsync();
        await Expect(page.GetByLabel("Local note", new() { Exact = true })).ToHaveValueAsync("private offline note");
        await page.GetByRole(AriaRole.Button, new() { Name = "Lock", Exact = true }).ClickAsync();
        await Expect(page.GetByLabel("Recovery key")).ToHaveValueAsync("");
    }

    [Theory(DisplayName = "T51 I01 I13: the published PWA reloads offline with its notes and uploads offline notes on reconnect")]
    [MemberData(nameof(OfflineReloadBrowsers))]
    public async Task WorksOfflineAfterReload(string browser)
    {
        var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await using var _ = context;
        var page = await OpenControlledAppAsync(context);
        await AddNoteAsync(page, $"online {browser}");
        await Expect(page.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 20_000 });

        await context.SetOfflineAsync(true);
        await page.ReloadAsync();
        await Expect(page.GetByTestId("note-text").Filter(new() { HasText = $"online {browser}" })).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await AddNoteAsync(page, $"offline {browser}");
        await Expect(page.GetByTestId("pending")).ToHaveTextAsync("1");
        await Expect(page.GetByTestId("status")).ToContainTextAsync("Offline", new() { Timeout = 20_000 });

        await context.SetOfflineAsync(false);
        await Expect(page.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 20_000 });

        var phone = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await using var __ = phone;
        var other = await OpenAppAsync(phone);
        await Expect(other.GetByTestId("note-text").Filter(new() { HasText = $"offline {browser}" })).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Expect(other.GetByTestId("note-text").Filter(new() { HasText = $"online {browser}" })).ToBeVisibleAsync();
    }

    [Theory(DisplayName = "I11 I16: concurrent edits on two devices are kept as a conflict in the UI and the user's choice wins everywhere")]
    [MemberData(nameof(Browsers))]
    public async Task ConflictIsShownAndResolved(string browser)
    {
        var laptop = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        var phone = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await using var _ = laptop;
        await using var __ = phone;
        var onLaptop = await OpenAppAsync(laptop);
        var onPhone = await OpenAppAsync(phone);
        await AddNoteAsync(onLaptop, "shopping list");
        await Expect(onLaptop.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 20_000 });
        await Expect(onPhone.GetByTestId("note-text").Filter(new() { HasText = "shopping list" })).ToBeVisibleAsync(new() { Timeout = 30_000 });

        // The laptop edits offline while the phone edits the same note online.
        await laptop.SetOfflineAsync(true);
        await EditAsync(onLaptop, "shopping list", "milk and eggs");
        await Expect(onLaptop.GetByTestId("pending")).ToHaveTextAsync("1");
        await EditAsync(onPhone, "shopping list", "bread");
        await Expect(onPhone.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 20_000 });

        await laptop.SetOfflineAsync(false);
        var conflict = onLaptop.GetByTestId("conflict");
        await Expect(conflict).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(conflict.GetByTestId("conflict-local")).ToHaveTextAsync("milk and eggs");
        await Expect(conflict.GetByTestId("conflict-server")).ToHaveTextAsync("bread");
        await Expect(onLaptop.GetByTestId("note-text")).ToHaveTextAsync("bread"); // the server state is shown meanwhile
        await Expect(onLaptop.GetByTestId("note-status")).ToContainTextAsync("conflict");
        await Expect(onLaptop.GetByTestId("pending")).ToHaveTextAsync("0");

        await conflict.GetByTestId("keep-mine").ClickAsync();
        await Expect(onLaptop.GetByTestId("conflict")).ToHaveCountAsync(0);
        await Expect(onLaptop.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 20_000 });
        await Expect(onPhone.GetByTestId("note-text")).ToHaveTextAsync("milk and eggs", new() { Timeout = 30_000 });
        await Expect(onLaptop.GetByTestId("note-text")).ToHaveTextAsync("milk and eggs");
    }

    private static async Task EditAsync(IPage page, string current, string replacement)
    {
        var note = page.GetByTestId("note").Filter(new() { Has = page.GetByTestId("note-text").Filter(new() { HasText = current }) });
        await note.GetByTestId("edit").ClickAsync();
        await note.GetByTestId("edit-text").FillAsync(replacement);
        await note.GetByTestId("save-edit").ClickAsync();
        await Expect(page.GetByTestId("note-text").Filter(new() { HasText = replacement })).ToBeVisibleAsync();
    }

    [Theory(DisplayName = "T54 I17: a service-worker update waits for the user and keeps the notes")]
    [MemberData(nameof(Browsers))]
    public async Task UpdateKeepsNotes(string browser)
    {
        var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await using var _ = context;
        var page = await OpenControlledAppAsync(context);
        await AddNoteAsync(page, $"before update {browser}");

        var worker = Path.Combine(PublishDir, "wwwroot", "service-worker.js");
        var original = await File.ReadAllTextAsync(worker);
        try
        {
            await File.WriteAllTextAsync(worker, original + $"\n// update {Guid.NewGuid()}\n");
            await page.EvaluateAsync("() => window.bsyncCheckForUpdate()");
            await Expect(page.Locator("#update-banner")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            await page.Locator("#apply-update").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.Load);
            await Expect(page.Locator("#update-banner")).ToBeHiddenAsync(new() { Timeout = 30_000 });
            await Expect(page.GetByTestId("note-text").Filter(new() { HasText = $"before update {browser}" })).ToBeVisibleAsync(new() { Timeout = 60_000 });
        }
        finally
        {
            await File.WriteAllTextAsync(worker, original);
        }
    }

    [Theory(DisplayName = "T12 T35 I10 I14: after a server restart that lost its data, unsynced notes are kept and uploaded; lost synced notes are hidden, not resurrected")]
    [MemberData(nameof(Browsers))]
    public async Task ServerRestart(string browser)
    {
        var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await using var _ = context;
        var page = await OpenAppAsync(context);
        await AddNoteAsync(page, $"synced {browser}");
        // The aggregate pending counter can still display its initial zero while the newly saved row appears.
        // Wait for that specific row's persisted clean state before declaring it acknowledged.
        await Expect(page.GetByTestId("note").Filter(new() { HasText = $"synced {browser}" }).GetByTestId("note-status"))
            .ToHaveCountAsync(0, new() { Timeout = 20_000 });
        await Expect(page.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 20_000 });

        // Stop the real endpoint; WebKit's emulated offline mode is not reliable for all worker requests.
        await StopServerAsync();
        await AddNoteAsync(page, $"unsynced {browser}");
        await Expect(page.GetByTestId("note").Filter(new() { HasText = $"unsynced {browser}" }).GetByTestId("note-status"))
            .ToContainTextAsync("not synced yet");
        await StartServerAsync();

        try
        {
            await Expect(page.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 30_000 });
            await Expect(page.GetByTestId("note-text").Filter(new() { HasText = $"unsynced {browser}" })).ToBeVisibleAsync();
            await Expect(page.GetByTestId("note-text").Filter(new() { HasText = $"synced {browser}" }).Filter(new() { HasNotText = "unsynced" })).ToHaveCountAsync(0, new() { Timeout = 20_000 });
        }
        catch (PlaywrightException error)
        {
            var status = await page.GetByTestId("status").TextContentAsync();
            var notes = await page.GetByTestId("note-text").AllTextContentsAsync();
            using var http = new HttpClient { BaseAddress = Address };
            using var request = new HttpRequestMessage(HttpMethod.Post, "sync/collections/notes/pull")
            {
                Content = new StringContent("""{"checkpoint":null,"limit":100}""", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Bsync-Protocol", "1");
            request.Headers.Add("Bsync-Schema", "notes-v1");
            var feed = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
            throw new PlaywrightException($"{error.Message}\nstatus: {status}\nnotes: {string.Join(" | ", notes)}\nserver feed: {feed}");
        }
    }
}
