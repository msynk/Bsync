namespace Bsync;

/// <summary>A local write was attempted on a <see cref="SyncMode.PullOnly"/> replica. Nothing was stored or queued.</summary>
public sealed class SyncReadOnlyException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public SyncReadOnlyException()
        : base("This collection is pull-only: it replicates server data and accepts no local writes.")
    {
    }
}
