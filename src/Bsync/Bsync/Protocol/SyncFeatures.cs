namespace Bsync.Protocol;

/// <summary>Optional protocol features a server advertises in <see cref="PullResult{TDocument}.Features"/>.</summary>
public static class SyncFeatures
{
    /// <summary>The server applies dependency groups atomically (protocol §4.1).</summary>
    public const string Groups = "groups";

    /// <summary>The pull response carries the server's <see cref="SyncLimits"/>; replicas clamp their batch sizes to them.</summary>
    public const string Limits = "limits";
}
