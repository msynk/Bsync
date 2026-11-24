using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bsync.Server.AspNetCore;

/// <summary>Options for one mapped collection.</summary>
public sealed class SyncEndpointOptions
{
    /// <summary>
    /// Application schema identifiers this server accepts in the <c>Bsync-Schema</c> header. Other
    /// values are refused with <c>upgrade-required</c> before anything is read or written.
    /// </summary>
    public required IReadOnlySet<string> SupportedSchemas { get; init; }

    /// <summary>
    /// Derives the caller's scope (for example a tenant id) from the authenticated request. Return
    /// <see langword="null"/> to refuse the request with <c>forbidden</c>. Never read the scope from the
    /// request body or an unauthenticated header. Required: there is no default scope (plan item A1). Use
    /// <see cref="SharedScope"/> only when every caller may read and write the same data; mapping then logs a warning.
    /// </summary>
    public required Func<HttpContext, string?> ResolveScope { get; init; }

    /// <summary>
    /// Whether callers without an authenticated identity reach the scope resolver. Default <see langword="false"/>:
    /// every Bsync route (pull, push, hints, pull batch, blobs) answers <c>401 unauthorized</c> to an anonymous caller,
    /// whether or not an authorization policy is put on the route group. Set it only for public data, demos and tests.
    /// </summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// A resolver that puts every caller in <paramref name="scope"/>, so all callers share one data set. Mapping a
    /// route with it logs a warning; prefer a resolver that reads a tenant or user claim.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is not a valid identifier.</exception>
    public static Func<HttpContext, string?> SharedScope(string scope = "default")
    {
        if (!SyncIds.IsValid(scope))
        {
            throw new ArgumentException("The scope must be a valid identifier.", nameof(scope));
        }

        return new SharedScopeResolver(scope).Resolve;
    }

    /// <summary>The single scope of a <see cref="SharedScope"/> resolver, or null for any other resolver.</summary>
    internal string? SharedScopeName => (ResolveScope.Target as SharedScopeResolver)?.Scope;

    /// <summary>Whether the request may reach the scope resolver (A1).</summary>
    internal bool Admits(HttpContext http) => AllowAnonymous || http.User.Identity?.IsAuthenticated == true;

    /// <summary>Logs once per mapping when the resolver shares one scope between every caller (A1).</summary>
    internal void WarnIfShared(ILogger logger, string route)
    {
        if (SharedScopeName is { } shared)
        {
            ServerLog.SharedScope(logger, route, shared, AllowAnonymous);
        }
    }

    private sealed class SharedScopeResolver(string scope)
    {
        public string Scope { get; } = scope;

        public string? Resolve(HttpContext http) => Scope;
    }

    /// <summary>Maximum request body size in bytes. Larger bodies get <c>payload-too-large</c>. Default 4 MiB.</summary>
    public long MaxRequestBodyBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// How many collections of one pull batch (<c>POST {prefix}/pull</c>) are pulled from their authorities at the same
    /// time. Default 4. A batch never has more parts than the group maps collections.
    /// </summary>
    public int MaxConcurrentBatchPulls
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "At least one pull must run at a time.");
    } = 4;

    /// <summary>
    /// Records which content each replica reached (task H, optional): after a pull that leaves nothing more to fetch,
    /// from a replica that sends its id, an acknowledgement goes to this store. A failure to record is logged and does
    /// not fail the pull.
    /// </summary>
    public ISyncReplicaAudit? ReplicaAudit { get; init; }

    /// <summary>The clock of <see cref="ReplicaAudit"/> timestamps (tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
