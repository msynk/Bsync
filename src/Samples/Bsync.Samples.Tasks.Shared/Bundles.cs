using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Clocks;

namespace Bsync.Samples.Tasks;

/// <summary>
/// A bundle (task F2): a set of files that is only useful complete, such as a handbook or a price list with its images.
/// The server publishes a new <see cref="Revision"/> as one manifest document; a device keeps using the previous revision
/// until every item of the new one is on the device and verified, then switches in one step.
/// </summary>
public sealed class BundleManifest : ISyncEntity
{
    /// <inheritdoc />
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    public HlcTimestamp UpdatedAt { get; set; }

    /// <inheritdoc />
    public bool Deleted { get; set; }

    /// <summary>Incremented by the server for every published version.</summary>
    public long Revision { get; set; }

    /// <summary>The files; <see cref="BlobReference.FileName"/> is the item's name within the bundle.</summary>
    public List<BlobReference> Items { get; set; } = [];

    /// <summary>Members added by newer versions of the app, kept so this version never erases them (I17).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A request to publish a bundle's next revision from content the tenant has uploaded.</summary>
/// <param name="Id">The bundle.</param>
/// <param name="Items">The files of the new revision.</param>
public sealed record PublishBundle(string Id, IReadOnlyList<BlobReference> Items);

/// <summary>What a device holds of a bundle.</summary>
/// <param name="Id">The bundle.</param>
/// <param name="ActiveRevision">The revision the device uses, or <see langword="null"/> before the first one is complete.</param>
/// <param name="LatestRevision">The newest revision the device knows of, or <see langword="null"/> if none.</param>
/// <param name="Missing">Items of the latest revision not yet on the device.</param>
/// <param name="BytesRemaining">The bytes still to download for the latest revision.</param>
public sealed record BundleState(string Id, long? ActiveRevision, long? LatestRevision, IReadOnlyList<string> Missing, long BytesRemaining)
{
    /// <summary>Whether the device uses the latest revision it knows of.</summary>
    public bool IsCurrent => LatestRevision is not null && ActiveRevision == LatestRevision;

    /// <summary>Whether any revision is usable.</summary>
    public bool IsAvailable => ActiveRevision is not null;
}
