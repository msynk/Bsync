using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bsync.Client;

/// <summary>Stable local application/environment/account boundaries. Never use this client identity as server authorization.</summary>
public sealed record AccountScope
{
    /// <summary>Creates a scope. Values are case-sensitive; use stable identifiers rather than display names.</summary>
    public AccountScope(string application, string environment, string account, string? organization = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (organization is not null) ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        Application = application;
        Environment = environment;
        Account = account;
        Organization = organization;
        // Length framing distinguishes delimiters, empty organization and all component boundaries.
        var framed = string.Concat(new[] { application, environment, account, organization }
            .Select(value => value is null ? "-1:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));
        StorageKey = Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(framed)));
    }

    /// <summary>Stable application identifier.</summary>
    public string Application { get; }
    /// <summary>Stable environment identifier (for example production or staging).</summary>
    public string Environment { get; }
    /// <summary>Stable account identifier.</summary>
    public string Account { get; }
    /// <summary>Optional organization boundary.</summary>
    public string? Organization { get; }
    /// <summary>Opaque filename/database-safe partition key. Hashing is not encryption or authorization.</summary>
    public string StorageKey { get; }
}
