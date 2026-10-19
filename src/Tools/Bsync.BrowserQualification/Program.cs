using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Playwright;

var engineOptions = args.Where(value => value.StartsWith("--engines=", StringComparison.Ordinal)).ToArray();
if (engineOptions.Length > 1) return 2;
var engineNames = (engineOptions.SingleOrDefault()?["--engines=".Length..] ?? "chromium,firefox,webkit").Split(',');
if (engineNames.Distinct().Count() != engineNames.Length || engineNames.Any(name => name is not ("chromium" or "firefox" or "webkit"))) return 2;
args = args.Except(engineOptions).ToArray();
var persistent = args.LastOrDefault() == "--persistent";
const int caseTimeoutMinutes = 20;
if (persistent) args = args[..^1];
if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: <published-wwwroot> <new-output-directory> [comma-separated-MiB=1,16,17,256,1024] [--persistent] [--engines=chromium,firefox,webkit]");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var sizes = (args.Length == 3 ? args[2] : "1,16,17,256,1024").Split(',').Select(int.Parse).ToArray();
if (!File.Exists(Path.Combine(root, "index.html")) || Directory.Exists(output) || sizes.Any(n => n is < 1 or > 1024)) return 2;
Directory.CreateDirectory(output);
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { WebRootPath = root });
builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
app.UseDefaultFiles(); app.UseStaticFiles(new StaticFileOptions { ServeUnknownFileTypes = true });
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var playwright = await Playwright.CreateAsync();
var results = new List<BrowserCase>();
var passed = true;
// Expected identities come from the native platform digest, independently of the browser implementation.
var referenceBuffer = new byte[1048576];
for (var i = 0; i < referenceBuffer.Length; i++) referenceBuffer[i] = (byte)(i % 251);
var referenceHashes = sizes.Distinct().ToDictionary(size => size, size =>
{
    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    for (var i = 0; i < size; i++) digest.AppendData(referenceBuffer);
    return Convert.ToHexStringLower(digest.GetHashAndReset());
});
foreach (var engine in new[] { playwright.Chromium, playwright.Firefox, playwright.Webkit }.Where(engine => engineNames.Contains(engine.Name)))
foreach (var size in sizes)
{
    // Fresh engine process for every object size, so a prior WASM high-water mark cannot contaminate it.
    IBrowser? browser = null;
    IBrowserContext? context = null;
    IPage? page = null;
    string? profile = null;
    var casePath = Path.Combine(output, $"{engine.Name}-{size}.json");
    var result = new BrowserCase { Engine = engine.Name, SizeMiB = size,
        StorageMode = persistent ? "temporary-persistent-profile" : "private-context" };
    try
    {
        Console.WriteLine($"Starting {engine.Name} {size} MiB ({result.StorageMode})");
        var launchArgs = engine.Name == "chromium" ? new[] { "--enable-precise-memory-info" } : null;
        if (persistent)
        {
            profile = Directory.CreateTempSubdirectory("bsync-browser-qualification-").FullName;
            context = await engine.LaunchPersistentContextAsync(profile, new() { Headless = true, Args = launchArgs });
        }
        else
        {
            browser = await engine.LaunchAsync(new() { Headless = true, Args = launchArgs });
            context = await browser.NewContextAsync();
        }
        await context.AddInitScriptAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "instrument.js")));
        page = await context.NewPageAsync();
        page.PageError += (_, error) => Console.Error.WriteLine($"{engine.Name} {size} MiB page error: {error}");
        page.Console += (_, message) =>
        {
            if (message.Text.StartsWith("bsync-qualification:", StringComparison.Ordinal))
                Console.WriteLine($"{engine.Name} {size} MiB {message.Text}");
        };
        page.SetDefaultTimeout(120_000);
        await page.GotoAsync(address);
        Console.WriteLine($"Loaded {engine.Name} {size} MiB host");
        await page.WaitForFunctionAsync("() => window.bsyncReady === true");
        Console.WriteLine($"Ready {engine.Name} {size} MiB runtime");
        result.BrowserVersion = (browser ?? context.Browser)?.Version ?? await page.EvaluateAsync<string>("() => navigator.userAgent");
        result.StorageAtStart = await page.EvaluateAsync<JsonElement>("async () => navigator.storage?.estimate ? await navigator.storage.estimate() : null");
        var managedJson = await page.EvaluateAsync<string>("""
            ([size, hash]) => DotNet.invokeMethodAsync('Bsync.Tests.BrowserHost', 'QualifyChunkBuffers', crypto.randomUUID(), size, hash)
            """, new object[] { size, referenceHashes[size] }).WaitAsync(TimeSpan.FromMinutes(caseTimeoutMinutes));
        var browserMemory = await page.EvaluateAsync<JsonElement>("() => window.bsyncQualificationFinish()");
        if (browserMemory.GetProperty("observedWasmMemories").GetInt32() == 0)
            throw new InvalidDataException("WASM memory observation unavailable.");
        result.Passed = true;
        result.Managed = JsonSerializer.Deserialize<JsonElement>(managedJson);
        result.BrowserMemory = browserMemory;
    }
    catch (Exception error)
    {
        result.Passed = false;
        result.Error = error.ToString();
        try { if (page is not null) result.BrowserMemory = await page.EvaluateAsync<JsonElement>("() => window.bsyncQualificationFinish()").WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* Preserve the original failure even if the browser process has stopped responding. */ }
    }
    finally
    {
        try { if (page is not null) result.StorageAtEnd = await page.EvaluateAsync<JsonElement>("async () => navigator.storage?.estimate ? await navigator.storage.estimate() : null").WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* A crashed page may no longer expose its quota estimate. */ }
        try
        {
            try { if (context is not null) await context.DisposeAsync(); }
            finally { if (browser is not null) await browser.DisposeAsync(); }
            if (profile is not null)
            {
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var resolved = Path.GetFullPath(profile);
                if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("bsync-browser-qualification-", StringComparison.Ordinal))
                    throw new IOException("Unexpected profile path; refusing recursive cleanup.");
                for (var attempt = 0; ; attempt++)
                {
                    try { Directory.Delete(resolved, recursive: true); break; }
                    catch (Exception error) when (attempt < 9 && error is IOException or UnauthorizedAccessException)
                    { await Task.Delay(200); }
                }
                result.ProfileRemoved = true;
            }
        }
        catch (Exception error) { result.Passed = false; result.Error += "\nProfile/browser cleanup: " + error; }
    }
    passed &= result.Passed;
    Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {engine.Name} {size} MiB ({result.StorageMode})");
    await File.WriteAllTextAsync(casePath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    results.Add(result);
}
var manifest = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order()
    .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
{
    Passed = passed, CreatedUtc = DateTimeOffset.UtcNow, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, HostFiles = manifest, Results = results,
    StorageMode = persistent ? "temporary-persistent-profile" : "private-context",
    CaseTimeoutMinutes = caseTimeoutMinutes,
    Engines = engineNames,
    ToolAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(BrowserCase).Assembly.Location))),
    InstrumentationSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "instrument.js")))),
    Scope = "Desktop browser engines. Sampled memory/ciphertext-provider I/O; no physical-device, flash-write or total browser/native-memory qualification. Cancellation is pre-admission only."
}, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

internal sealed class BrowserCase
{
    public required string Engine { get; init; }
    public string? BrowserVersion { get; set; }
    public int SizeMiB { get; init; }
    public required string StorageMode { get; init; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public JsonElement? Managed { get; set; }
    public JsonElement? BrowserMemory { get; set; }
    public JsonElement? StorageAtStart { get; set; }
    public JsonElement? StorageAtEnd { get; set; }
    public bool? ProfileRemoved { get; set; }
}
