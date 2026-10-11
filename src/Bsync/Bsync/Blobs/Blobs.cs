namespace Bsync.Blobs;

/// <summary>
/// An attachment named by a document (task F1). The bytes never enter the document's JSON: they live in content-addressed
/// stores on the device (<see cref="IBlobCache"/>) and on the server, identified by <see cref="Sha256"/>.
/// </summary>
/// <param name="Id">The attachment's id within its document.</param>
/// <param name="Sha256">The SHA-256 of the content, lowercase hexadecimal.</param>
/// <param name="Size">The content length in bytes.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="FileName">The original file name, for display.</param>
public sealed record BlobReference(string Id, string Sha256, long Size, string ContentType, string FileName);

/// <summary>The state of an upload, as the server reports it.</summary>
/// <param name="Received">How many bytes of the content the server holds.</param>
/// <param name="Complete">Whether the server holds the verified content for the caller.</param>
/// <param name="UploadUrl">
/// A short-lived presigned URL to which the whole content may be sent in one <c>PUT</c> instead of in chunks, when the
/// server's store is an object store that offers them; <see langword="null"/> otherwise.
/// </param>
public sealed record BlobUploadStatus(long Received, bool Complete, Uri? UploadUrl = null);

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
public sealed class FileBlobSource : IBlobSource
{
    private readonly string _path;

    /// <summary>Creates the source.</summary>
    public FileBlobSource(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        Length = new FileInfo(path).Length;
    }

    /// <inheritdoc />
    public long Length { get; }

    /// <inheritdoc />
    public ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        stream.Position = offset;
        return ValueTask.FromResult<Stream>(stream);
    }
}

/// <summary>Thrown when transferred bytes do not hash to the expected SHA-256. Nothing was kept.</summary>
public sealed class BlobCorruptedException(string message) : IOException(message);

/// <summary>
/// The device's content-addressed store of attachment bytes (task F1). Content becomes readable only after its SHA-256 was
/// checked, so a partial or corrupted transfer is never opened as complete; a transfer in progress is kept as a partial
/// and continues after a restart. Implemented by <see cref="FileBlobCache"/> (native) and by the browser store in
/// <c>Bsync.Blazor</c>.
/// </summary>
public interface IBlobCache
{
    /// <summary>The size of the verified content, or <see langword="null"/> when it is not on this device.</summary>
    Task<long?> SizeAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>How many bytes of an unfinished transfer of <paramref name="sha256"/> are kept.</summary>
    Task<long> PartialLengthAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>Appends bytes to the unfinished transfer of <paramref name="sha256"/>, durably.</summary>
    Task AppendPartialAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>Verifies the unfinished transfer and makes it the content; <see langword="false"/> (and discarded) on a mismatch.</summary>
    Task<bool> CompletePartialAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>Discards the unfinished transfer of <paramref name="sha256"/>.</summary>
    Task DiscardPartialAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>Opens verified content (seekable), or returns <see langword="null"/> when it is not on this device.</summary>
    Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores <paramref name="content"/>, durably and verified, and returns its SHA-256 and size. Save the document that
    /// names it only after this returns, so a saved reference always has its bytes.
    /// </summary>
    Task<(string Sha256, long Size)> ImportAsync(Stream content, CancellationToken cancellationToken = default);

    /// <summary>Removes verified content (eviction, wipe).</summary>
    Task DeleteAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>The hashes of the verified content on this device.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);
}
