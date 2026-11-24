using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Clocks;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>A document type exercising the JSON rules of docs/protocol.md.</summary>
public sealed class FixtureDoc : ISyncEntity
{
    public string Id { get; set; } = string.Empty;

    public HlcTimestamp UpdatedAt { get; set; }

    public bool Deleted { get; set; }

    public string? Title { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset? Due { get; set; }

    public List<string> Tags { get; set; } = [];

    /// <summary>Fields unknown to this client; preserved so a full-document write does not erase them (I17).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
