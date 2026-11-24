namespace Bsync;

/// <summary>Request-level error codes (docs/protocol.md §7). Operation-level codes are in <c>PushErrorCodes</c>.</summary>
public static class SyncErrorCodes
{
    /// <summary>The checkpoint cannot be served; the replica must reset.</summary>
    public const string ResetRequired = "reset-required";

    /// <summary>The protocol or application schema version is not supported.</summary>
    public const string UpgradeRequired = "upgrade-required";

    /// <summary>The request exceeds the authority's limits.</summary>
    public const string PayloadTooLarge = "payload-too-large";

    /// <summary>Too many requests; retry after the indicated delay.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>Credentials are missing or expired.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>The caller may not use this collection or scope.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The request is malformed.</summary>
    public const string InvalidRequest = "invalid-request";

    /// <summary>The authority is temporarily unavailable.</summary>
    public const string Unavailable = "unavailable";
}
