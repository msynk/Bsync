using Microsoft.Data.SqlClient;
using Microsoft.Playwright;
using Xunit;

namespace Bsync.Tests.Browser;

/// <summary>
/// The published tasks sample (SQL Server system of record, WebAssembly client) with attachments (task F1): bytes are
/// kept in IndexedDB, uploaded before the task that names them, and downloaded on another device.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class TasksSampleTests(BrowserFixture fixture) : IAsyncLifetime
{
    private readonly string _database = $"bs_browser_{Guid.NewGuid():N}";
    private readonly string _blobs = Path.Combine(Path.GetTempPath(), "bsync-tasks-blobs-" + Guid.NewGuid().ToString("N"));
    private SampleServer? _server;

    public static TheoryData<string> Browsers() => new() { "chromium", "firefox", "webkit" };

    private static string? Admin => Environment.GetEnvironmentVariable("BSYNC_SQLSERVER");

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Admin))
        {
            return;
        }

        await ExecuteAsync($"CREATE DATABASE [{_database}]");
        _server = new SampleServer("tasks-sample", "Bsync.Samples.Tasks.Server.dll", new Dictionary<string, string>
        {
            ["ConnectionStrings__Tasks"] = new SqlConnectionStringBuilder(Admin) { InitialCatalog = _database }.ConnectionString,
            ["Tasks__BlobDirectory"] = _blobs,
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is null)
        {
            return;
        }

        await _server.DisposeAsync();
        SqlConnection.ClearAllPools();
        await ExecuteAsync($"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END");
        try
        {
            Directory.Delete(_blobs, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);

    private async Task<IPage> SignInAsync(IBrowserContext context, string user)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(_server!.Address.ToString());
        await page.GetByTestId("user").FillAsync(user, new() { Timeout = 60_000 });
        await page.GetByTestId("tenant").FillAsync("team-1");
        await page.GetByTestId("sign-in").ClickAsync();
        await Expect(page.GetByTestId("status")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        return page;
    }

    [SqlServerTheory(DisplayName = "F1 I01 I13: an attachment added while the server is unreachable waits with its task, is uploaded on reconnect, and opens verified on another device after a reload")]
    [MemberData(nameof(Browsers))]
    public async Task AttachmentsReachOtherDevices(string browser)
    {
        var text = $"minutes of the {browser} meeting";
        var laptop = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await using var _ = laptop;
        var onLaptop = await SignInAsync(laptop, "alice");
        await onLaptop.GetByTestId("new-title").FillAsync("Write report");
        await onLaptop.GetByTestId("add").ClickAsync();
        await Expect(onLaptop.GetByTestId("task-title")).ToHaveTextAsync("Write report");
        await Expect(onLaptop.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 30_000 });

        // Attached while the server is unreachable: the bytes are on the laptop, and the task waits for them to reach the
        // server. (Routes are blocked instead of emulating offline mode: WebKit then fails to read the chosen file.)
        await laptop.RouteAsync("**/sync/**", route => route.AbortAsync("internetdisconnected"));
        await onLaptop.GetByTestId("attach").SetInputFilesAsync(new FilePayload { Name = "minutes.txt", MimeType = "text/plain", Buffer = System.Text.Encoding.UTF8.GetBytes(text) });
        await Expect(onLaptop.GetByTestId("attachment-name")).ToHaveTextAsync("minutes.txt");
        await Expect(onLaptop.GetByTestId("attachment-open")).ToBeVisibleAsync();
        await Expect(onLaptop.GetByTestId("pending")).ToHaveTextAsync("1");
        Assert.False(Directory.Exists(Path.Combine(_blobs, "objects")) && Directory.EnumerateFiles(Path.Combine(_blobs, "objects"), "*", SearchOption.AllDirectories).Any());

        await laptop.UnrouteAsync("**/sync/**");
        await Expect(onLaptop.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 60_000 });
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_blobs, "objects"), "*", SearchOption.AllDirectories));

        // Another device sees the task at once and downloads the attachment on demand.
        var phone = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await using var __ = phone;
        var onPhone = await SignInAsync(phone, "bob");
        await Expect(onPhone.GetByTestId("attachment-name")).ToHaveTextAsync("minutes.txt", new() { Timeout = 30_000 });
        await onPhone.GetByTestId("attachment-download").ClickAsync();
        await onPhone.GetByTestId("attachment-open").ClickAsync(new() { Timeout = 30_000 });
        await Expect(onPhone.GetByTestId("attachment-preview")).ToHaveTextAsync(text);

        // The verified bytes stay on the device: after a reload (and signing in again) they open offline.
        await onPhone.CloseAsync();
        onPhone = await SignInAsync(phone, "bob");
        await phone.RouteAsync("**/sync/**", route => route.AbortAsync("internetdisconnected"));
        await onPhone.GetByTestId("attachment-open").ClickAsync(new() { Timeout = 30_000 });
        await Expect(onPhone.GetByTestId("attachment-preview")).ToHaveTextAsync(text);
    }

    [SqlServerTheory]
    [MemberData(nameof(Browsers))]
    public async Task ExpiredBlobCredentialsPreserveAttachmentAndResumeAfterSignIn(string browser)
    {
        await using var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        var page = await SignInAsync(context, "alice");
        await page.GetByTestId("new-title").FillAsync("Credential recovery");
        await page.GetByTestId("add").ClickAsync();
        await Expect(page.GetByTestId("task-title")).ToHaveTextAsync("Credential recovery");
        await Expect(page.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 30000 });
        await context.RouteAsync("**/sync/blobs/**", route => route.FulfillAsync(new() { Status = 401, Body = "" }));
        await page.GetByTestId("attach").SetInputFilesAsync(new FilePayload
        { Name = "retained.txt", MimeType = "text/plain", Buffer = "Kept through credential expiry"u8.ToArray() });
        await Expect(page.GetByTestId("attachment-transfer")).ToHaveTextAsync("Sign in again to upload", new() { Timeout = 30000 });
        await Expect(page.GetByTestId("pending")).ToHaveTextAsync("1");
        await page.GetByTestId("attachment-open").ClickAsync();
        await Expect(page.GetByTestId("attachment-preview")).ToHaveTextAsync("Kept through credential expiry");
        await page.CloseAsync();
        await context.UnrouteAsync("**/sync/blobs/**");
        page = await SignInAsync(context, "alice");
        await Expect(page.GetByTestId("attachment-transfer")).ToHaveTextAsync("Uploaded", new() { Timeout = 60000 });
        await Expect(page.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 30000 });
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_blobs, "objects"), "*", SearchOption.AllDirectories));
    }

    [SqlServerTheory]
    [MemberData(nameof(Browsers))]
    public async Task SwitchingBrowserSessionsPreservesPendingWorkUnderItsOriginalActor(string browser)
    {
        await using var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        await context.RouteAsync("**/sync/**", route => route.AbortAsync("internetdisconnected"));
        var alice = await SignInAsync(context, "alice");
        await alice.GetByTestId("new-title").FillAsync("Alice's pending work");
        await alice.GetByTestId("add").ClickAsync();
        await Expect(alice.GetByTestId("task-title")).ToHaveTextAsync("Alice's pending work");
        await Expect(alice.GetByTestId("pending")).ToHaveTextAsync("1");
        await alice.CloseAsync();

        // A fresh application lifetime replaces credentials; the old replica is never reused under the new actor.
        var bob = await SignInAsync(context, "bob");
        await bob.GetByTestId("new-title").FillAsync("Bob's own work");
        await bob.GetByTestId("add").ClickAsync();
        await Expect(bob.GetByTestId("task-title")).ToHaveTextAsync("Bob's own work");
        await Expect(bob.GetByTestId("pending")).ToHaveTextAsync("1");
        await bob.CloseAsync();

        alice = await SignInAsync(context, "alice");
        await Expect(alice.GetByTestId("task-title")).ToHaveTextAsync("Alice's pending work");
        await context.UnrouteAsync("**/sync/**");
        await Expect(alice.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 60000 });
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(Admin) { InitialCatalog = _database }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT ChangedBy FROM dbo.Tasks WHERE Title=@title", connection);
        command.Parameters.AddWithValue("@title", "Alice's pending work");
        Assert.Equal("alice", await command.ExecuteScalarAsync());
        await alice.CloseAsync();
        bob = await SignInAsync(context, "bob");
        await Expect(bob.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 60000 });
        await Expect(bob.GetByTestId("task-title")).ToHaveCountAsync(2, new() { Timeout = 30000 });
        command.Parameters["@title"].Value = "Bob's own work";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        object? actor;
        while ((actor = await command.ExecuteScalarAsync()) is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal("bob", actor);
    }

    [SqlServerTheory]
    [MemberData(nameof(Browsers))]
    public async Task OfflineActionShowsLocalTransportAndServerExecutionSeparately(string browser)
    {
        await using var context = await (await fixture.BrowserAsync(browser)).NewContextAsync();
        var page = await SignInAsync(context, "alice");
        await page.GetByTestId("new-title").FillAsync("Complete the inspection");
        await page.GetByTestId("add").ClickAsync();
        await Expect(page.GetByTestId("task-title")).ToHaveTextAsync("Complete the inspection");
        await Expect(page.GetByTestId("pending")).ToHaveTextAsync("0", new() { Timeout = 30000 });
        await context.RouteAsync("**/sync/**", route => route.AbortAsync("internetdisconnected"));
        await page.GetByTestId("task-complete").ClickAsync();
        await Expect(page.GetByTestId("intent-local")).ToHaveTextAsync("Saved on this device");
        await Expect(page.GetByTestId("intent-transport")).ToContainTextAsync("Pending");
        await Expect(page.GetByTestId("intent-execution")).ToContainTextAsync("Awaiting server outcome");
        // Reload and re-authenticate against the development token endpoint while replication remains blocked.
        await page.CloseAsync();
        page = await SignInAsync(context, "alice");
        await Expect(page.GetByTestId("intent-local")).ToHaveTextAsync("Saved on this device");
        await Expect(page.GetByTestId("intent-execution")).ToContainTextAsync("Awaiting server outcome");
        await context.UnrouteAsync("**/sync/**");
        await Expect(page.GetByTestId("intent-execution")).ToContainTextAsync("Succeeded", new() { Timeout = 60000 });
        await Expect(page.GetByTestId("intent-transport")).ToContainTextAsync("Synced");
        await Expect(page.GetByTestId("task-done")).ToHaveTextAsync("Complete", new() { Timeout = 30000 });
    }
}
