namespace Bsync.Client;

/// <summary>
/// Physical account lifetime exclusion. Shared access permits concurrent local replicas; exclusive access is required
/// for deletion/migration. Every participant must use the same namespace outside deleted account resources. Ownership
/// never expires while resources are open; release after closing all resources or on process/context termination.
/// </summary>
public interface IAccountResourceLock
{
    /// <summary>Acquires immediately or returns null. Exclusive access conflicts with all holders; shared holders coexist.</summary>
    ValueTask<IAsyncDisposable?> TryAcquireAsync(AccountScope account, bool exclusive, CancellationToken cancellationToken = default);
}

/// <summary>Another process/tab still uses account resources; no deletion was performed by this attempt.</summary>
public sealed class AccountResourcesInUseException() : Exception("Account resources are in use; close other account lifetimes and retry.");

/// <summary>Native account lifetime exclusion through shared/exclusive file handles. Lock files are never deleted.</summary>
public sealed class FileAccountResourceLock : IAccountResourceLock
{
    private readonly string _directory;

    /// <summary>Uses a shared lock directory outside every account's deletable files.</summary>
    public FileAccountResourceLock(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable?> TryAcquireAsync(AccountScope account, bool exclusive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_directory, account.StorageKey + ".account-lock");
        try
        {
            // The stable file is only bootstrapped, never replaced/unlinked. A competing exclusive holder refuses this
            // initialization too. Once present, shared acquisitions need only read access to the existing file.
            if (!File.Exists(path))
                using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            return ValueTask.FromResult<IAsyncDisposable?>(new FileStream(path, FileMode.Open,
                exclusive ? FileAccess.ReadWrite : FileAccess.Read, exclusive ? FileShare.None : FileShare.Read));
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
        { return ValueTask.FromResult<IAsyncDisposable?>(null); }
    }
}
