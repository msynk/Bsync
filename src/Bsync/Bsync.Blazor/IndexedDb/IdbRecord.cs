using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Bsync.Clocks;
using Microsoft.JSInterop;

namespace Bsync.Blazor.IndexedDb;

/// <summary>Serialized form of one record in IndexedDB. 64-bit numbers are strings.</summary>
internal sealed class IdbRecord
{
    public string Id { get; set; } = string.Empty;

    public string? Stamp { get; set; }

    public string Current { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;

    public bool Deleted { get; set; }

    public string? Base { get; set; }

    public string? BaseVersion { get; set; }

    public bool IsDirty { get; set; }

    public string LocalRevision { get; set; } = "0";

    public string? PendingId { get; set; }

    public string? PendingRevision { get; set; }

    public string? PendingBaseVersion { get; set; }

    public string? PendingPayload { get; set; }

    public string? RejectionRevision { get; set; }

    public string? RejectionCode { get; set; }

    public string? RejectionMessage { get; set; }

    public Dictionary<string, string>? RejectionArguments { get; set; }

    public string? Observed { get; set; }

    public string? ObservedVersion { get; set; }

    public string Generation { get; set; } = "0";

    public bool Missing { get; set; }

    public string? ConflictServer { get; set; }

    public string? ConflictServerVersion { get; set; }

    public string? ConflictLocal { get; set; }

    public string? ConflictBase { get; set; }

    public string? GroupId { get; set; }

    public List<string>? GroupMembers { get; set; }

    public string? PendingGroup { get; set; }

    public int? PendingGroupSize { get; set; }

    /// <summary>Encoded keys of the declared indexes (ADR-018), by index name.</summary>
    public Dictionary<string, string>? IndexKeys { get; set; }
}

/// <summary>A live record as read for an index rebuild.</summary>
internal sealed record IdbLiveRecord(string Id, string Stamp, string Current);

/// <summary>Index keys to store for a record, if it still has <see cref="Stamp"/>.</summary>
internal sealed record IdbIndexKeys(string Id, string Stamp, Dictionary<string, string> IndexKeys);
