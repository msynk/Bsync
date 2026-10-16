using Bsync.Protocol;
using Bsync.Server.AspNetCore;
using Bsync.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace Bsync.Tests.Browser;

/// <summary>
/// Serves the published WebAssembly harness and the sync endpoints from one Kestrel host on localhost, and
/// owns the Playwright browsers. The authority behind the endpoints can be replaced (restore) or slowed.
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private readonly Dictionary<string, IBrowser> _browsers = new(StringComparer.Ordinal);
    private WebApplication? _app;
    private IPlaywright? _playwright;

    public Uri BaseAddress { get; private set; } = null!;

    public ControllableAuthority Authority { get; } = new();

    public async Task InitializeAsync()
    {
        var exit = Microsoft.Playwright.Program.Main(["install", "chromium", "firefox", "webkit"]);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Playwright browser installation failed with exit code {exit}.");
        }

        var webRoot = Environment.GetEnvironmentVariable("BSYNC_BROWSER_HOST") is { Length: > 0 } packagedHost
            ? Path.GetFullPath(packagedHost) : Path.Combine(AppContext.BaseDirectory, "browserhost", "wwwroot");
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            throw new InvalidOperationException($"The browser harness was not published to {webRoot}.");
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { WebRootPath = webRoot });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions { ServeUnknownFileTypes = true });
        app.MapSyncCollection("notes", Authority, SyncJsonTypes<ConformanceDocument>.From(ConformanceJsonContext.Default), new SyncEndpointOptions
        {
            SupportedSchemas = new HashSet<string>(StringComparer.Ordinal) { "notes-v1" },
        });
        await app.StartAsync();
        _app = app;
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

        _playwright = await Playwright.CreateAsync();
    }

    public async Task<IBrowser> BrowserAsync(string name)
    {
        if (!_browsers.TryGetValue(name, out var browser))
        {
            var type = name switch
            {
                "chromium" => _playwright!.Chromium,
                "firefox" => _playwright!.Firefox,
                "webkit" => _playwright!.Webkit,
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };
            browser = await type.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            _browsers[name] = browser;
        }

        return browser;
    }

    public async Task DisposeAsync()
    {
        foreach (var browser in _browsers.Values)
        {
            await browser.CloseAsync();
        }

        _playwright?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
