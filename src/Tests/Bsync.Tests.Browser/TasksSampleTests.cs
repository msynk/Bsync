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
}
