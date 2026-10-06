using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bsync.Diagnostics;

namespace Bsync.Blobs;

/// <summary>Options of <see cref="HttpBlobTransfer"/>.</summary>
public sealed class HttpBlobTransferOptions
{
    /// <summary>The blob routes' prefix (<c>MapSyncBlobs</c>'s <c>prefix</c>). Default <c>sync/blobs</c>.</summary>
    public string BasePath { get; init; } = "sync/blobs";

    /// <summary>The size of one upload request and of one appended download chunk. Default 4 MiB.</summary>
    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Whether content is sent straight to a presigned URL when the server offers one (its store is an object store), so
    /// the bytes do not pass through the application server. Such an upload is one request; after an interruption it
    /// starts again from the beginning. Default <see langword="true"/>.
    /// </summary>
    public bool DirectUploads { get; init; } = true;

    /// <summary>
    /// The client for presigned URLs. It must not add the sync credentials (an object store refuses a second
    /// authorization). Default a shared client without handlers.
    /// </summary>
    public HttpClient? DirectClient { get; init; }
}

/// <summary>
/// Resumable, chunked transfers against the blob routes (protocol §8.6, task F1). Every step can fail (network loss,
/// process death); calling again continues from what the other side already holds. When the server offers presigned
/// URLs, downloads follow its redirect and uploads go straight to the object store (see
/// <see cref="HttpBlobTransferOptions.DirectUploads"/>). Records <c>bsync.blob.bytes</c> and
/// <c>bsync.blob.transfers</c> on the <c>Bsync</c> meter.
/// </summary>
public sealed class HttpBlobTransfer
{
    private static readonly Lazy<HttpClient> SharedDirectClient = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });

    private readonly HttpClient _http;
    private readonly HttpBlobTransferOptions _options;
    private readonly string _base;

    /// <summary>Creates the transfer client on <paramref name="http"/> (credentials as for sync).</summary>
    public HttpBlobTransfer(HttpClient http, HttpBlobTransferOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _options = options ?? new HttpBlobTransferOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ChunkSize, 1024, nameof(options));
        _base = _options.BasePath.Trim('/');
    }

    /// <summary>
    /// Uploads <paramref name="source"/> as <paramref name="sha256"/>, continuing a partial upload, and finishes it. A finish
    /// whose response was lost is safe to repeat: the server keeps one object per hash. Returns the bytes sent by this call.
    /// </summary>
    /// <exception cref="BlobCorruptedException">The server discarded the upload: the bytes did not match the hash.</exception>
    public async Task<long> UploadAsync(string sha256, IBlobSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sent = 0L;
        var result = "failed";
        try
        {
            var status = await StatusAsync(await _http.PostAsync($"{_base}/{sha256}/uploads?size={source.Length}", null, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var buffer = new byte[(int)Math.Min(_options.ChunkSize, Math.Max(source.Length, 1))];
            while (!status.Complete)
            {
                if (status.UploadUrl is { } direct && status.Received == 0 && _options.DirectUploads)
                {
                    // Straight to the object store, in one request; the server verifies the hash at finish as usual.
                    await using (var stream = await source.OpenAsync(0, cancellationToken).ConfigureAwait(false))
                    {
                        using var whole = new StreamContent(stream);
                        whole.Headers.ContentLength = source.Length;
                        whole.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        using var stored = await (_options.DirectClient ?? SharedDirectClient.Value).PutAsync(direct, whole, cancellationToken).ConfigureAwait(false);
                        stored.EnsureSuccessStatusCode();
                    }

                    sent += source.Length;
                    Bytes.Add(source.Length, Up);
                    status = new BlobUploadStatus(source.Length, false);
                    continue;
                }

                if (status.Received >= source.Length)
                {
                    var finish = await _http.PostAsync($"{_base}/{sha256}/uploads/finish", null, cancellationToken).ConfigureAwait(false);
                    if (finish.StatusCode == HttpStatusCode.UnprocessableEntity)
                    {
                        result = "corrupt";
                        throw new BlobCorruptedException($"The server discarded the upload of {sha256}: the bytes did not match the hash.");
                    }

                    status = await StatusAsync(finish, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                int count;
                await using (var stream = await source.OpenAsync(status.Received, cancellationToken).ConfigureAwait(false))
                {
                    count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, source.Length - status.Received)), 1, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                }

                using var content = new ByteArrayContent(buffer, 0, count);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = await _http.PutAsync($"{_base}/{sha256}/uploads/{status.Received}", content, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    sent += count;
                    Bytes.Add(count, Up);
                }

                // 409: another device moved the upload on; continue from where the server is.
                status = await StatusAsync(response, cancellationToken, HttpStatusCode.Conflict).ConfigureAwait(false);
            }

            result = "complete";
            return sent;
        }
        finally
        {
            Transfers.Add(1, Up, new KeyValuePair<string, object?>("bsync.result", result));
        }
    }

    /// <summary>Uploads verified content of <paramref name="cache"/>, continuing a partial upload.</summary>
    public async Task<long> UploadAsync(IBlobCache cache, string sha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var size = await cache.SizeAsync(sha256, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The content {sha256} is not on this device.");
        return await UploadAsync(sha256, new CacheSource(cache, sha256, size), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the blob into <paramref name="cache"/>, continuing what it already holds, and verifies it (range requests;
    /// a server may redirect to a presigned URL). Returns the bytes received by this call; 0 when the content was already
    /// there. On a hash mismatch the partial transfer is discarded and <see cref="BlobCorruptedException"/> is thrown.
    /// </summary>
    public async Task<long> DownloadAsync(IBlobCache cache, string sha256, long size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (await cache.SizeAsync(sha256, cancellationToken).ConfigureAwait(false) is not null)
        {
            return 0;
        }

        var received = 0L;
        var result = "failed";
        try
        {
            var held = await cache.PartialLengthAsync(sha256, cancellationToken).ConfigureAwait(false);
            if (held > size)
            {
                await cache.DiscardPartialAsync(sha256, cancellationToken).ConfigureAwait(false);
                held = 0;
            }

            if (held < size)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_base}/{sha256}");
                if (held > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(held, null);
                }

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (held > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new HttpRequestException($"The server did not resume the download at {held} ({(int)response.StatusCode}).", null, response.StatusCode);
                }

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[_options.ChunkSize];
                while (held < size)
                {
                    // Whatever arrives is kept in chunks, so an interrupted download continues from it.
                    var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - held)), 1, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await cache.AppendPartialAsync(sha256, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    held += read;
                    received += read;
                    Bytes.Add(read, Down);
                }

                if (held < size)
                {
                    throw new IOException($"The download of {sha256} ended at {held} of {size} bytes.");
                }
            }

            if (!await cache.CompletePartialAsync(sha256, cancellationToken).ConfigureAwait(false))
            {
                result = "corrupt";
                throw new BlobCorruptedException($"The downloaded content of {sha256} does not match its hash; it was discarded.");
            }

            result = "complete";
            return received;
        }
        finally
        {
            Transfers.Add(1, Down, new KeyValuePair<string, object?>("bsync.result", result));
        }
    }

    private static readonly KeyValuePair<string, object?> Up = new("bsync.direction", "upload");
    private static readonly KeyValuePair<string, object?> Down = new("bsync.direction", "download");

    private static readonly System.Diagnostics.Metrics.Counter<long> Bytes = SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.blob.bytes", "By", "Attachment bytes transferred, by direction (upload, download).");

    private static readonly System.Diagnostics.Metrics.Counter<long> Transfers = SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.blob.transfers", "{transfer}", "Attachment transfers, by direction and result (complete, failed, corrupt).");

    private static async Task<BlobUploadStatus> StatusAsync(HttpResponseMessage response, CancellationToken cancellationToken, HttpStatusCode? alsoAccepted = null)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != alsoAccepted)
            {
                response.EnsureSuccessStatusCode();
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var direct = root.TryGetProperty("uploadUrl", out var url) && url.ValueKind == JsonValueKind.String ? new Uri(url.GetString()!, UriKind.Absolute) : null;
            return new BlobUploadStatus(root.GetProperty("received").GetInt64(), root.GetProperty("complete").GetBoolean(), direct);
        }
    }

    /// <summary>Verified content of a cache, as an upload source.</summary>
    private sealed class CacheSource(IBlobCache cache, string sha256, long length) : IBlobSource
    {
        public long Length => length;

        public async ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default)
        {
            var stream = await cache.OpenReadAsync(sha256, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The content {sha256} is not on this device.");
            stream.Seek(offset, SeekOrigin.Begin);
            return stream;
        }
    }
}
