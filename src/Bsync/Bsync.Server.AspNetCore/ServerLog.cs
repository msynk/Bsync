using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bsync.Server.AspNetCore;

/// <summary>Structured log messages for the endpoints. Bodies, document ids and principals are never logged.</summary>
internal static partial class ServerLog
{
    [LoggerMessage(EventId = 1, EventName = "SyncRequestRefused", Message = "Sync {Endpoint} for '{Collection}' refused: {Status} {Code} {Reason}")]
    public static partial void Refused(ILogger logger, LogLevel level, string collection, string endpoint, int status, string code, string? reason);

    [LoggerMessage(EventId = 2, EventName = "SyncAuthorityFailed", Level = LogLevel.Error, Message = "Sync {Endpoint} for '{Collection}' failed in the authority.")]
    public static partial void AuthorityFailed(ILogger logger, string collection, string endpoint, Exception error);

    [LoggerMessage(EventId = 3, EventName = "SyncSharedScope", Level = LogLevel.Warning, Message = "Every caller of the Bsync routes '{Route}' shares the scope '{Scope}' (anonymous callers allowed: {AllowAnonymous}). Resolve the scope from a tenant or user claim unless all callers may read and write the same data.")]
    public static partial void SharedScope(ILogger logger, string route, string scope, bool allowAnonymous);
}
