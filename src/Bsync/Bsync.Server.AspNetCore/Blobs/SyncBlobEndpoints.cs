using System.Diagnostics.Metrics;
using Bsync.Blobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bsync.Server.AspNetCore.Blobs;

/// <summary>Options of the blob routes.</summary>
public sealed class SyncBlobEndpointOptions
{
    /// <summary>The largest blob accepted. Default 200 MiB.</summary>
    public long MaxSize { get; init; } = 200L * 1024 * 1024;

    /// <summary>How long a presigned download URL is valid, when the store offers them. Default five minutes.</summary>
    public TimeSpan PresignedReadValidity { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a presigned upload URL is valid, when the store offers them. Default fifteen minutes.</summary>
    public TimeSpan PresignedWriteValidity { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The blob routes (protocol §8.6, task F1). Uploads are resumable and per scope: a scope proves it has the content by
/// uploading it, even when another scope already stored the same bytes, so a hash never reveals what others hold; the
/// object itself is stored once. Reads are allowed by <see cref="IBlobAccess.CanReadAsync"/> and answered with 404
/// otherwise (whether content exists is not disclosed). Records <c>bsync.server.blob.bytes</c> and
/// <c>bsync.server.blob.requests</c> on the <c>Bsync.Server</c> meter.
/// </summary>
public static class SyncBlobEndpoints
{
    private static readonly Counter<long> Bytes = SyncEndpoints.ServerMeter.CreateCounter<long>(
        "bsync.server.blob.bytes", "By", "Attachment bytes received and served (not counting presigned downloads), by direction.");

    private static readonly Counter<long> Requests = SyncEndpoints.ServerMeter.CreateCounter<long>(
        "bsync.server.blob.requests", "{request}", "Blob requests, by endpoint and result.");

    /// <summary>
    /// Maps <c>POST {prefix}/{sha256}/uploads</c>, <c>PUT {prefix}/{sha256}/uploads/{offset}</c>,
    /// <c>POST {prefix}/{sha256}/uploads/finish</c> and <c>GET {prefix}/{sha256}</c>. The caller's scope comes from
    /// <paramref name="options"/> (<see cref="SyncEndpointOptions.ResolveScope"/>), as for the sync routes.
    /// </summary>
    /// <returns>The route group, for adding authorization.</returns>
    public static RouteGroupBuilder MapSyncBlobs(
        this IEndpointRouteBuilder endpoints,
        IBlobStore store,
        IBlobAccess access,
        SyncEndpointOptions options,
        SyncBlobEndpointOptions? blobOptions = null,
        string prefix = "sync/blobs")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(options);
        blobOptions ??= new SyncBlobEndpointOptions();
        var group = endpoints.MapGroup($"{prefix.Trim('/')}/{{sha256}}");

        // Starts or resumes an upload: how much the server already holds, and a presigned upload URL when the store offers
        // one and nothing is held yet.
        group.MapPost("/uploads", (RequestDelegate)(async http =>
        {
            if (Caller(http, options) is not { } caller || !long.TryParse(http.Request.Query["size"], out var size) || size < 0)
            {
                await RefuseAsync(http, "start", StatusCodes.Status400BadRequest);
                return;
            }

            if (size > blobOptions.MaxSize)
            {
                await RefuseAsync(http, "start", StatusCodes.Status413PayloadTooLarge, BlobCodes.TooLarge);
                return;
            }

            Requests.Add(1, Tag("start"), Ok);
            var sha256 = Sha(http);
            if (await access.HoldsAsync(caller, sha256, http.RequestAborted))
            {
                await StatusAsync(http, StatusCodes.Status200OK, size, complete: true);
            }
            else
            {
                var upload = FileSystemBlobStore.UploadKey(caller.Scope, sha256);
                var received = await store.GetReceivedAsync(upload, http.RequestAborted);
                var direct = received == 0 ? await store.PresignWriteAsync(upload, size, blobOptions.PresignedWriteValidity, http.RequestAborted) : null;
                await StatusAsync(http, StatusCodes.Status200OK, received, complete: false, direct);
            }
        }));

        // Appends one chunk at the offset the client believes the server is at; 409 with the real offset otherwise.
        group.MapPut("/uploads/{offset:long}", (RequestDelegate)(async http =>
        {
            if (Caller(http, options) is not { } caller || !long.TryParse(http.Request.RouteValues["offset"]?.ToString(), out var offset) || offset < 0)
            {
                await RefuseAsync(http, "append", StatusCodes.Status400BadRequest);
                return;
            }

            var upload = FileSystemBlobStore.UploadKey(caller.Scope, Sha(http));
            try
            {
                if (await store.AppendAsync(upload, offset, http.Request.Body, blobOptions.MaxSize, http.RequestAborted) is { } received)
                {
                    Bytes.Add(received - offset, new KeyValuePair<string, object?>("bsync.direction", "upload"));
                    Requests.Add(1, Tag("append"), Ok);
                    await StatusAsync(http, StatusCodes.Status200OK, received, complete: false);
                    return;
                }

                Requests.Add(1, Tag("append"), Result("offset-mismatch"));
                await StatusAsync(http, StatusCodes.Status409Conflict, await store.GetReceivedAsync(upload, http.RequestAborted), complete: false);
            }
            catch (InvalidDataException)
            {
                await RefuseAsync(http, "append", StatusCodes.Status413PayloadTooLarge, BlobCodes.TooLarge);
            }
        }));

        // Verifies the hash and records that this scope holds the content. Repeating it (a lost response) is harmless.
        group.MapPost("/uploads/finish", (RequestDelegate)(async http =>
        {
            if (Caller(http, options) is not { } caller)
            {
                await RefuseAsync(http, "finish", StatusCodes.Status400BadRequest);
                return;
            }

            var sha256 = Sha(http);
            if (await access.HoldsAsync(caller, sha256, http.RequestAborted))
            {
                Requests.Add(1, Tag("finish"), Ok);
                await StatusAsync(http, StatusCodes.Status200OK, 0, complete: true);
                return;
            }

            var upload = FileSystemBlobStore.UploadKey(caller.Scope, sha256);
            var size = await store.GetReceivedAsync(upload, http.RequestAborted);
            if (size > blobOptions.MaxSize)
            {
                // Only possible through a presigned upload, which the store cannot limit; the collector removes it.
                await RefuseAsync(http, "finish", StatusCodes.Status413PayloadTooLarge, BlobCodes.TooLarge);
                return;
            }

            if (!await store.CompleteAsync(upload, sha256, http.RequestAborted))
            {
                await RefuseAsync(http, "finish", StatusCodes.Status422UnprocessableEntity, BlobCodes.HashMismatch);
                return;
            }

            await access.RecordAsync(caller, sha256, size, http.RequestAborted);
            Requests.Add(1, Tag("finish"), Ok);
            await StatusAsync(http, StatusCodes.Status200OK, size, complete: true);
        }));

        // Reads a blob the caller may read: a redirect to a presigned URL when the store has them, else the bytes, with
        // range requests (resumable downloads) either way.
        group.MapGet("/", (RequestDelegate)(async http =>
        {
            if (Caller(http, options) is not { } caller)
            {
                await RefuseAsync(http, "read", StatusCodes.Status400BadRequest);
                return;
            }

            var sha256 = Sha(http);
            if (!await access.CanReadAsync(caller, sha256, http.RequestAborted))
            {
                await RefuseAsync(http, "read", StatusCodes.Status404NotFound);
                return;
            }

            if (await store.PresignReadAsync(sha256, blobOptions.PresignedReadValidity, http.RequestAborted) is { } direct)
            {
                Requests.Add(1, Tag("read"), Result("redirect"));
                await Results.Redirect(direct.ToString(), permanent: false, preserveMethod: true).ExecuteAsync(http);
                return;
            }

            if (await store.OpenReadAsync(sha256, http.RequestAborted) is not { } stream)
            {
                await RefuseAsync(http, "read", StatusCodes.Status404NotFound);
                return;
            }

            Requests.Add(1, Tag("read"), Ok);
            await Results.Stream(new CountingStream(stream), "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(http);
        }));

        return group;
    }

    private static readonly KeyValuePair<string, object?> Ok = Result("ok");

    private static KeyValuePair<string, object?> Tag(string endpoint) => new("bsync.endpoint", endpoint);

    private static KeyValuePair<string, object?> Result(string result) => new("bsync.result", result);

    private static string Sha(HttpContext http) => (string)http.Request.RouteValues["sha256"]!;

    private static Task RefuseAsync(HttpContext http, string endpoint, int status, string? code = null)
    {
        Requests.Add(1, Tag(endpoint), Result(code ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        http.Response.StatusCode = status;
        if (code is null)
        {
            return Task.CompletedTask;
        }

        http.Response.ContentType = "application/problem+json";
        return WriteJsonAsync(http, writer =>
        {
            writer.WriteNumber("status", status);
            writer.WriteString("title", code);
            writer.WriteString("code", code);
        });
    }

    private static Task StatusAsync(HttpContext http, int status, long received, bool complete, Uri? uploadUrl = null)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        return WriteJsonAsync(http, writer =>
        {
            writer.WriteNumber("received", received);
            writer.WriteBoolean("complete", complete);
            if (uploadUrl is not null)
            {
                writer.WriteString("uploadUrl", uploadUrl.AbsoluteUri);
            }
        });
    }

    private static async Task WriteJsonAsync(HttpContext http, Action<System.Text.Json.Utf8JsonWriter> members)
    {
        http.Response.Headers.CacheControl = "no-store";
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        await using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }

        await http.Response.Body.WriteAsync(buffer.WrittenMemory, http.RequestAborted);
    }

    private static SyncCallContext? Caller(HttpContext http, SyncEndpointOptions options) =>
        http.Request.RouteValues["sha256"] is string sha256 && sha256.Length == 64 && sha256.All(char.IsAsciiHexDigitLower)
        && options.ResolveScope(http) is { } scope && SyncIds.IsValid(scope)
            ? new SyncCallContext(http.User, scope)
            : null;

    /// <summary>Counts the bytes served.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

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

        private static int Count(int read)
        {
            Bytes.Add(read, new KeyValuePair<string, object?>("bsync.direction", "download"));
            return read;
        }
    }
}
