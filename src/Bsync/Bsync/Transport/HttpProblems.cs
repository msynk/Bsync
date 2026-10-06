using System.Net;
using System.Text.Json;
using Bsync.Protocol;

namespace Bsync.Transport;

/// <summary>Maps HTTP problem answers of the binding to the transport's exceptions.</summary>
internal static class HttpProblems
{
    /// <summary>The protocol version this client speaks.</summary>
    public const string ProtocolVersion = "1";

    public static async Task<Exception> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var (code, detail, reason) = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
        var retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => (TimeSpan?)null,
        };
        return ToException(status, code, detail, reason, retryAfter);
    }

    /// <summary>The exception for a problem answer (also one collection's part of a pull batch).</summary>
    public static Exception ToException(int status, string? code, string? detail, string? reason, TimeSpan? retryAfter)
    {
        code ??= (HttpStatusCode)status switch
        {
            HttpStatusCode.Unauthorized => SyncErrorCodes.Unauthorized,
            HttpStatusCode.Forbidden => SyncErrorCodes.Forbidden,
            HttpStatusCode.RequestEntityTooLarge => SyncErrorCodes.PayloadTooLarge,
            HttpStatusCode.TooManyRequests => SyncErrorCodes.RateLimited,
            _ when status >= 500 => SyncErrorCodes.Unavailable,
            _ => SyncErrorCodes.InvalidRequest,
        };

        var message = detail ?? $"The server answered {status} ({code}).";
        if (code == SyncErrorCodes.ResetRequired)
        {
            return new SyncResetRequiredException(message, reason ?? ResetReasons.Epoch);
        }

        var transient = code is SyncErrorCodes.RateLimited or SyncErrorCodes.Unavailable || status >= 500;
        return new SyncTransportException(code, message, transient, retryAfter, status);
    }

    /// <summary>Reads the code, detail and reset reason of a problem element.</summary>
    public static (string? Code, string? Detail, string? Reason) ReadProblem(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return (Text(root, "code"), Text(root, "detail"), Text(root, "reason"));
    }

    private static async Task<(string? Code, string? Detail, string? Reason)> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 64 * 1024)
            {
                return (null, null, null);
            }

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            static string? Text(JsonElement root, string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (Text(root, "code"), Text(root, "detail"), Text(root, "reason"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }
}
