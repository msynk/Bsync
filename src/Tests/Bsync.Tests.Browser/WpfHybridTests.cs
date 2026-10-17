using System.Diagnostics;
using Xunit;

namespace Bsync.Tests.Browser;

/// <summary>
/// ADR-007 native host: the WPF Blazor Hybrid sample (WebView2, SQLite replica, HTTP) driven through its real UI by its
/// smoke mode: an online write reaches the server; a write made while the server is down survives an app restart and
/// is uploaded when the server is back.
/// </summary>
public sealed class WpfHybridTests : IAsyncLifetime
{
    private readonly SampleServer _server = new("notes-sample", "Bsync.Samples.Notes.Server.dll");
    private readonly string _data = Path.Combine(Path.GetTempPath(), "bsync-wpf", Guid.NewGuid().ToString("N"));

    public static string Executable { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Samples", "Bsync.Samples.Hybrid.Wpf", "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0-windows10.0.19041.0", "Bsync.Samples.Hybrid.Wpf.exe"));

    public Task InitializeAsync() => _server.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> RunAsync(string step, string text)
    {
        var result = Path.Combine(_data, $"{step}.txt");
        Directory.CreateDirectory(_data);
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var argument in new[] { "--server", _server.Address.ToString(), "--data", Path.Combine(_data, "replica"), "--smoke", result, "--step", step, "--text", text })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return File.Exists(result) ? await File.ReadAllTextAsync(result) : $"no result (exit code {process.ExitCode})";
    }

    private async Task<string> ServerFeedAsync()
    {
        using var http = new HttpClient { BaseAddress = _server.Address };
        using var request = new HttpRequestMessage(HttpMethod.Post, "sync/collections/notes/pull")
        {
            Content = new StringContent("""{"checkpoint":null,"limit":100}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Bsync-Protocol", "1");
        request.Headers.Add("Bsync-Schema", "notes-v1");
        return await (await http.SendAsync(request)).Content.ReadAsStringAsync();
    }

    [WindowsDesktopFact(DisplayName = "ADR-007 I01 I16: the WPF Hybrid app writes through its UI, keeps an offline write across a restart and uploads it later")]
    public async Task HybridAppSyncs()
    {
        var online = await RunAsync("online", "written in WPF");
        Assert.StartsWith("ok", online);
        Assert.Contains("host=native renderer=WebView", online);
        Assert.Contains("written in WPF", await ServerFeedAsync());

        await _server.StopAsync();
        Assert.StartsWith("ok", await RunAsync("offline", "written offline in WPF"));
        await _server.StartAsync();

        // The in-memory sample server lost its data when it stopped; the replica resets, keeps its pending note and uploads it.
        Assert.StartsWith("ok", await RunAsync("resume", "written offline in WPF"));
        Assert.Contains("written offline in WPF", await ServerFeedAsync());
    }

    [WindowsDesktopFact]
    public async Task NativeContentLibrarySurvivesInterruptedUpdateAndOfflineProcessRestart()
    {
        Assert.StartsWith("ok", await RunAsync("library-prepare", ""));
        await _server.StopAsync();
        Assert.StartsWith("ok", await RunAsync("library-resume", ""));
    }
}
