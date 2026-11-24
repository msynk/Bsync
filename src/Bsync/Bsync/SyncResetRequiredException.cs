namespace Bsync;

/// <summary>
/// Thrown when the server can no longer serve the client's checkpoint. The engine then resnapshots, keeping
/// pending local work (docs/protocol.md §6.1).
/// </summary>
public sealed class SyncResetRequiredException : SyncProtocolException
{
    /// <summary>Creates the exception.</summary>
    public SyncResetRequiredException(string message, string reason = ResetReasons.Epoch)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Why the checkpoint cannot be served; one of <see cref="ResetReasons"/>.</summary>
    public string Reason { get; }
}
