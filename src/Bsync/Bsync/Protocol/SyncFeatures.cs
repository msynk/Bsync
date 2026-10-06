namespace Bsync.Protocol;

/// <summary>Optional protocol features a server advertises in <see cref="PullResult{TDocument}.Features"/>.</summary>
public static class SyncFeatures
{
    /// <summary>The server applies dependency groups atomically (protocol §4.1).</summary>
    public const string Groups = "groups";

    /// <summary>The pull response carries the server's <see cref="SyncLimits"/>; replicas clamp their batch sizes to them.</summary>
    public const string Limits = "limits";

    /// <summary>
    /// The pull response carries the server's time (<see cref="PullResult{TDocument}.ServerTime"/>); replicas correct their
    /// clock with it so the server does not reject their writes as too far in the future.
    /// </summary>
    public const string ServerTime = "server-time";

    /// <summary>
    /// Documents that leave the caller's view are listed in <see cref="PullResult{TDocument}.Removals"/> instead of forcing a
    /// <c>scope-changed</c> reset (ADR-015). A replica asks for it in <see cref="PullRequest.Features"/>.
    /// </summary>
    public const string Removals = "removals";

    /// <summary>
    /// The server offers one hint stream for several collections (<c>GET {prefix}/hints?collections=a,b</c>, task C3); a
    /// coordinator opens it instead of one stream per collection.
    /// </summary>
    public const string HintsMultiplex = "hints-multiplex";

    /// <summary>
    /// The server offers <c>POST {prefix}/pull</c> for the collections of one group: pages of several collections in one
    /// request (task C3; protocol §8.5).
    /// </summary>
    public const string PullBatch = "pull-batch";

    /// <summary>
    /// The pull response carries the server's retention horizon (<see cref="PullResult{TDocument}.RetentionHorizon"/>):
    /// replicas drop their own clean tombstones at or below it, which the server has already forgotten (task D4).
    /// </summary>
    public const string Retention = "retention";
}
