using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Bsync.Samples.Tasks;

/// <summary>
/// An attachment named by a document (task F1). The bytes never enter the document's JSON: they live in content-addressed
/// blob stores on the device and on the server, identified by <see cref="Sha256"/>.
/// </summary>
/// <param name="Id">The attachment's id within its document.</param>
/// <param name="Sha256">The SHA-256 of the content, lowercase hexadecimal.</param>
/// <param name="Size">The content length in bytes.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="FileName">The original file name, for display.</param>
public sealed record BlobReference(string Id, string Sha256, long Size, string ContentType, string FileName);

/// <summary>The state of an upload, as the server reports it.</summary>
/// <param name="Received">How many bytes of the content the server holds.</param>
/// <param name="Complete">Whether the server holds the verified content (for this tenant).</param>
public sealed record BlobUploadStatus(long Received, bool Complete);

/// <summary>Stable codes of the blob routes.</summary>
public static class BlobCodes
{
    /// <summary>The uploaded bytes do not hash to the announced SHA-256; the server discarded them.</summary>
    public const string HashMismatch = "blob-hash-mismatch";

    /// <summary>The content is larger than the server accepts.</summary>
    public const string TooLarge = "blob-too-large";
}

/// <summary>Bytes to transfer: a length and a stream that can start at any offset (to resume).</summary>
public interface IBlobSource
{
    /// <summary>The content length in bytes.</summary>
    long Length { get; }

    /// <summary>Opens the content at <paramref name="offset"/>.</summary>
    ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default);
}

/// <summary>A file on disk.</summary>
public sealed class FileBlobSource(string path) : IBlobSource
{
    /// <inheritdoc />
    public long Length { get; } = new FileInfo(path).Length;

    /// <inheritdoc />
    public ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        stream.Position = offset;
        return ValueTask.FromResult<Stream>(stream);
    }
}

/// <summary>A blob on the server, read with HTTP range requests so a download resumes where it stopped.</summary>
public sealed class HttpBlobSource(HttpClient http, string sha256, long length) : IBlobSource
{
    /// <inheritdoc />
    public long Length => length;

    /// <inheritdoc />
    public async ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/blobs/{sha256}");
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new HttpRequestException($"The server did not resume the download at {offset} ({(int)response.StatusCode}).", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}

/// <summary>Thrown when transferred bytes do not hash to the expected SHA-256. Nothing was kept.</summary>
public sealed class BlobCorruptedException(string message) : IOException(message);

/// <summary>
/// Resumable, chunked transfers against the sample server's blob routes. Every step can fail (network loss, process
/// death); calling again continues from what the other side already holds.
/// </summary>
public sealed class BlobTransfer(HttpClient http, int chunkSize = 4 * 1024 * 1024)
{
    /// <summary>
    /// Uploads <paramref name="source"/> as <paramref name="sha256"/>, continuing a partial upload, and finishes it.
    /// A finish whose response was lost is safe to repeat: the server keeps one object per hash.
    /// </summary>
    /// <returns>The number of content bytes sent by this call.</returns>
    public async Task<long> UploadAsync(string sha256, IBlobSource source, CancellationToken cancellationToken = default)
    {
        var sent = 0L;
        var status = await ReadStatusAsync(await http.PostAsync($"api/blobs/{sha256}/uploads?size={source.Length}", null, cancellationToken), cancellationToken);
        var buffer = new byte[chunkSize];
        while (!status.Complete)
        {
            if (status.Received >= source.Length)
            {
                var finish = await http.PostAsync($"api/blobs/{sha256}/uploads/finish", null, cancellationToken);
                if (finish.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    throw new BlobCorruptedException($"The server discarded the upload of {sha256}: the bytes did not match the hash.");
                }

                status = await ReadStatusAsync(finish, cancellationToken);
                continue;
            }

            int count;
            await using (var stream = await source.OpenAsync(status.Received, cancellationToken))
            {
                count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(chunkSize, source.Length - status.Received)), 1, throwOnEndOfStream: false, cancellationToken);
            }

            using var content = new ByteArrayContent(buffer, 0, count);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var response = await http.PutAsync($"api/blobs/{sha256}/uploads/{status.Received}", content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                sent += count;
            }

            // 409: another device moved the upload on; continue from where the server is.
            status = await ReadStatusAsync(response, cancellationToken, HttpStatusCode.Conflict);
        }

        return sent;
    }

    /// <summary>
    /// Downloads the blob into <paramref name="partialPath"/>, continuing what is there, and verifies it. Returns the
    /// number of bytes received by this call. On a hash mismatch the partial file is deleted and
    /// <see cref="BlobCorruptedException"/> is thrown; the caller never sees unverified content as complete.
    /// </summary>
    public async Task<long> DownloadAsync(string sha256, long size, string partialPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        var received = 0L;
        await using (var file = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
        {
            if (file.Length > size)
            {
                file.SetLength(0);
            }

            if (file.Length < size)
            {
                file.Position = file.Length;
                await using var stream = await new HttpBlobSource(http, sha256, size).OpenAsync(file.Length, cancellationToken);
                var buffer = new byte[81920];
                int read;
                try
                {
                    while (file.Length < size && (read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        var useful = (int)Math.Min(read, size - file.Length);
                        await file.WriteAsync(buffer.AsMemory(0, useful), cancellationToken);
                        received += useful;
                    }
                }
                finally
                {
                    // Whatever arrived is kept (and flushed) for the next attempt.
                    await file.FlushAsync(CancellationToken.None);
                    file.Flush(flushToDisk: true);
                }
            }

            if (file.Length < size)
            {
                throw new IOException($"The download of {sha256} ended at {file.Length} of {size} bytes.");
            }

            file.Position = 0;
            if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken)), sha256, StringComparison.Ordinal))
            {
                file.Close();
                File.Delete(partialPath);
                throw new BlobCorruptedException($"The downloaded content of {sha256} does not match its hash; it was discarded.");
            }
        }

        return received;
    }

    private static async Task<BlobUploadStatus> ReadStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken, HttpStatusCode? alsoAccepted = null)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != alsoAccepted)
            {
                response.EnsureSuccessStatusCode();
            }

            return (await response.Content.ReadFromJsonAsync(TasksJson.Default.BlobUploadStatus, cancellationToken))!;
        }
    }
}
