using Bsync.Clocks;

namespace Bsync.Testing;

/// <summary>The document type used by the conformance cases.</summary>
public sealed class ConformanceDocument : ISyncEntity
{
    /// <inheritdoc />
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    public HlcTimestamp UpdatedAt { get; set; }

    /// <inheritdoc />
    public bool Deleted { get; set; }

    /// <summary>Payload used to detect aliasing and lost updates.</summary>
    public string Title { get; set; } = string.Empty;
}
