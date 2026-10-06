namespace Bsync;

/// <summary>Which way a replica synchronizes (<see cref="SyncOptions{TDocument}.Mode"/>).</summary>
public enum SyncMode
{
    /// <summary>Local writes are uploaded and server changes pulled. The default.</summary>
    TwoWay,

    /// <summary>
    /// A read-only replica of server data (for example a projection): only server changes are pulled, each record keeps
    /// only its current state (no base, pending or conflict copies), and local writes throw
    /// <see cref="SyncReadOnlyException"/>. The server must still refuse writes (<c>CanWrite</c>): this mode only stops this
    /// replica from making them.
    /// </summary>
    PullOnly,
}
