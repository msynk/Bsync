using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Blobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bsync.Server.AspNetCore.Blobs;

/// <summary>Additive, authenticated schema-1 multipart control routes. Existing blob routes are unchanged.</summary>
public static class SyncMultipartEndpoints
{
    /// <summary>
    /// Resolves the provider from current authenticated context on every request. The provider must bind durable sessions
    /// to that scope/account generation, enforce size limits, reconcile remote receipts, verify final bytes before granting
    /// access, and coordinate revocation with completion. Return null only for explicit unsupported capability (501).
    /// Never resolve a provider from a client-supplied scope or session id. Signed replies are marked no-store.
    /// </summary>
    public static RouteGroupBuilder MapSyncMultipartUploads(this IEndpointRouteBuilder endpoints,
        Func<HttpContext, CancellationToken, Task<IMultipartUploadProtocol?>> resolveAuthorizedProvider,
        string prefix = "sync/multipart")
    {
        ArgumentNullException.ThrowIfNull(resolveAuthorizedProvider);
        var group = endpoints.MapGroup(prefix.Trim('/')).RequireAuthorization();
        async Task<IMultipartUploadProtocol?> Resolve(HttpContext http)
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.User.Identity?.IsAuthenticated != true) { http.Response.StatusCode = 401; return null; }
            var hash = http.Request.RouteValues["sha256"]?.ToString();
            if (hash?.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))) { http.Response.StatusCode = 400; return null; }
            var provider = await resolveAuthorizedProvider(http, http.RequestAborted);
            if (provider is null) http.Response.StatusCode = 501;
            return provider;
        }
        static string Value(HttpContext http, string name) => http.Request.RouteValues[name]!.ToString()!;
        group.MapPost("/{sha256}/start/{operationId}", (RequestDelegate)(async http =>
        {
            if (await Resolve(http) is not { } provider) return;
            if (!Guid.TryParseExact(Value(http, "operationId"), "N", out _) || !long.TryParse(http.Request.Query["size"], out var size) || size < 0)
            { http.Response.StatusCode = 400; return; }
            var session = await provider.StartAsync(Value(http, "operationId"), Value(http, "sha256"), size, cancellationToken: http.RequestAborted);
            if (session is null) { http.Response.StatusCode = 501; return; }
            await http.Response.WriteAsJsonAsync(session, MultipartServerJson.Default.MultipartUploadSession, cancellationToken: http.RequestAborted);
        }));
        group.MapPost("/{sha256}/{sessionId}/part/{number:int}", (RequestDelegate)(async http =>
        {
            if (await Resolve(http) is not { } provider) return;
            if (!int.TryParse(Value(http, "number"), out var number) || number is < 1 or > 1000) { http.Response.StatusCode = 400; return; }
            var ticket = await provider.AuthorizePartAsync(Value(http, "sessionId"), Value(http, "sha256"), number, cancellationToken: http.RequestAborted);
            await http.Response.WriteAsJsonAsync(ticket, MultipartServerJson.Default.MultipartPartTicket, cancellationToken: http.RequestAborted);
        }));
        group.MapPost("/{sha256}/{sessionId}/complete", (RequestDelegate)(async http =>
        {
            if (await Resolve(http) is not { } provider) return;
            var session = await provider.CompleteAsync(Value(http, "sessionId"), Value(http, "sha256"), cancellationToken: http.RequestAborted);
            await http.Response.WriteAsJsonAsync(session, MultipartServerJson.Default.MultipartUploadSession, cancellationToken: http.RequestAborted);
        }));
        group.MapPost("/{sha256}/{sessionId}/abort", (RequestDelegate)(async http =>
        {
            if (await Resolve(http) is not { } provider) return;
            await provider.AbortAsync(Value(http, "sessionId"), Value(http, "sha256"), cancellationToken: http.RequestAborted);
            http.Response.StatusCode = 204;
        }));
        return group;
    }
}

[JsonSerializable(typeof(MultipartUploadSession))]
[JsonSerializable(typeof(MultipartPartTicket))]
internal partial class MultipartServerJson : JsonSerializerContext;
