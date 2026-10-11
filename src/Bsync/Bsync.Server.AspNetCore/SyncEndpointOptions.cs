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
    /// request body or an unauthenticated header. Default: <c>"default"</c> for every caller.
    /// </summary>
    public Func<HttpContext, string?> ResolveScope { get; init; } = static _ => "default";

    /// <summary>Maximum request body size in bytes. Larger bodies get <c>payload-too-large</c>. Default 4 MiB.</summary>
    public long MaxRequestBodyBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Records which content each replica reached (task H, optional): after a pull that leaves nothing more to fetch,
    /// from a replica that sends its id, an acknowledgement goes to this store. A failure to record is logged and does
    /// not fail the pull.
    /// </summary>
    public ISyncReplicaAudit? ReplicaAudit { get; init; }

    /// <summary>The clock of <see cref="ReplicaAudit"/> timestamps (tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
