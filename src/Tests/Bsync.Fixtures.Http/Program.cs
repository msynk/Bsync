using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Server.AspNetCore.Blobs;

// This fixture is deliberately loopback-only with a fixed scope. It is not an authentication/hosting example.
if (args.Length < 3) throw new ArgumentException("server|client <loopback-url> <isolated-directory> [tickets|unsupported]");
var address = new Uri(args[1]);
if (!address.IsLoopback || address.Scheme != "http") throw new ArgumentException("Only HTTP loopback is permitted.");
var directory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(directory);
if (args[0] == "server")
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls(address.AbsoluteUri);
    var app = builder.Build();
    var files = new FileSystemBlobStore(Path.Combine(directory, "server"));
    app.MapSyncBlobs(files, new Access(), new SyncEndpointOptions { SupportedSchemas = new HashSet<string>(["blobs-v1"]), ResolveScope = _ => "fixture" });
    app.MapGet("/health", () => typeof(HttpBlobTransfer).Assembly.GetName().Version!.ToString());
    await app.RunAsync();
    return;
}
if (args[0] != "client") throw new ArgumentException("Unknown fixture mode.");
var bytes = new byte[8193]; new Random(1729).NextBytes(bytes);
var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
var source = new FileBlobCache(Path.Combine(directory, "source"));
await source.ImportAsync(new MemoryStream(bytes));
using var http = new HttpClient { BaseAddress = address };
var options = new HttpBlobTransferOptions { ChunkSize = 1024
#if DOWNLOAD_TICKETS
    , DownloadTickets = args.Length > 3
#endif
};
var transfer = new HttpBlobTransfer(http, options);
await transfer.UploadAsync(source, hash);
var target = new FileBlobCache(Path.Combine(directory, "target"));
await target.AppendPartialAsync(hash, bytes.AsMemory(0, 3072));
if (args.Length > 3 && args[3] == "unsupported")
{
    try { await transfer.DownloadAsync(target, hash, bytes.Length); throw new InvalidOperationException("Old server unexpectedly supported tickets."); }
    catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
    if (await target.PartialLengthAsync(hash) != 3072 || await target.SizeAsync(hash) is not null)
        throw new InvalidOperationException("Unsupported route changed retained bytes.");
    Console.WriteLine("PASS: opted-in new client explicitly refused by old server; partial retained.");
    return;
}
if (await transfer.DownloadAsync(target, hash, bytes.Length) != bytes.Length - 3072)
    throw new InvalidOperationException("Range download did not resume.");
await using var result = await target.OpenReadAsync(hash) ?? throw new InvalidOperationException("Missing verified download.");
using var buffer = new MemoryStream(); await result.CopyToAsync(buffer);
if (!buffer.ToArray().SequenceEqual(bytes)) throw new InvalidOperationException("Downloaded bytes differ.");
Console.WriteLine($"PASS: upload and resumed download, client {typeof(HttpBlobTransfer).Assembly.GetName().Version}, mode {args.ElementAtOrDefault(3) ?? "legacy"}.");

sealed class Access : IBlobAccess
{
    private readonly ConcurrentDictionary<string, byte> _held = new();
    public Task<bool> HoldsAsync(SyncCallContext caller, string hash, CancellationToken cancellationToken = default) => Task.FromResult(_held.ContainsKey(hash));
    public Task<bool> CanReadAsync(SyncCallContext caller, string hash, CancellationToken cancellationToken = default) => HoldsAsync(caller, hash, cancellationToken);
    public Task RecordAsync(SyncCallContext caller, string hash, long size, CancellationToken cancellationToken = default)
    { _held[hash] = 0; return Task.CompletedTask; }
}
