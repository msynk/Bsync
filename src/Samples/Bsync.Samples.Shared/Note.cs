using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Clocks;

namespace Bsync.Samples.Shared;

/// <summary>A synchronized note.</summary>
public sealed class Note : ISyncEntity
{
    /// <inheritdoc />
    public string Id { get; set; } = Guid.CreateVersion7().ToString("N");

    /// <inheritdoc />
    public HlcTimestamp UpdatedAt { get; set; }

    /// <inheritdoc />
    public bool Deleted { get; set; }

    /// <summary>The note text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Fields added by newer versions of the app. Kept so that editing a note in an older version does not
    /// erase them (docs/protocol.md §2.1).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
