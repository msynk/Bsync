namespace Bsync.Blobs;

/// <summary>
/// Non-expiring mutual exclusion for a cache's physical operations. Every process/tab using a managed cache must use
/// the same lock namespace. Ownership ends only on disposal or process/context termination, never by a timer.
/// </summary>
public interface IBlobCacheLock
{
    /// <summary>Acquires a named exclusive lock: lowercase SHA-256, "metadata", or "lifetime-" plus a unique 64-digit lowercase hex token that is never reused.</summary>
    ValueTask<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Acquires immediately or returns null. Providers without a nonblocking probe conservatively return null.</summary>
    ValueTask<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult<IAsyncDisposable?>(null);
}

/// <summary>Native exclusive file locks. Stable cache lock files remain; unique lifetime lock files use delete-on-close.</summary>
public sealed class FileBlobCacheLock : IBlobCacheLock
{
    private readonly string _directory;
    /// <summary>Uses a dedicated lock directory shared by all users of this account's cache.</summary>
    public FileBlobCacheLock(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }
    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryAcquireAsync(name, cancellationToken).ConfigureAwait(false) is { } held) return held;
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }
    /// <inheritdoc />
    public ValueTask<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name); cancellationToken.ThrowIfCancellationRequested();
        try { return ValueTask.FromResult<IAsyncDisposable?>(new FileStream(Path.Combine(_directory, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            4096, name.StartsWith("lifetime-", StringComparison.Ordinal) ? FileOptions.DeleteOnClose : FileOptions.None)); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
        { return ValueTask.FromResult<IAsyncDisposable?>(null); }
    }
    /// <summary>Validates a portable lock name.</summary>
    public static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name == "metadata") return;
        if (name.StartsWith("lifetime-", StringComparison.Ordinal)) name = name[9..];
        if (name.Length != 64 || name.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Expected metadata, a lowercase SHA-256 or a unique lifetime token.", nameof(name));
    }
}
