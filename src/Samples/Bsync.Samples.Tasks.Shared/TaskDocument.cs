using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Clocks;

namespace Bsync.Samples.Tasks;

/// <summary>
/// The replicated shape of a task. The server's system of record is the <c>dbo.Tasks</c> table (EF Core); this document
/// is its replication projection (ADR-014).
/// </summary>
public sealed class TaskDocument : ISyncEntity
{
    /// <inheritdoc />
    public string Id { get; set; } = Guid.CreateVersion7().ToString();

    /// <inheritdoc />
    public HlcTimestamp UpdatedAt { get; set; }

    /// <inheritdoc />
    public bool Deleted { get; set; }

    /// <summary>What to do. Required (<see cref="TaskRules.TitleRequired"/>).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Whether the task is done.</summary>
    public bool Done { get; set; }

    /// <summary>Computed by the server from <see cref="Title"/>; whatever a client sends is replaced.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Set by the server: incremented by every accepted change. Intents name the revision the user saw.</summary>
    public long Revision { get; set; }

    /// <summary>Members added by newer versions of the app, kept so this version never erases them (I17).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
