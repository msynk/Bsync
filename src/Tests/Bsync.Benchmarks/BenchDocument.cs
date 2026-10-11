using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Running;
using Bsync;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Server;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

/// <summary>A document of about 1 KiB (ADR-012 functional workload).</summary>
public sealed class BenchDocument : ISyncEntity
{
    public string Id { get; set; } = string.Empty;

    public HlcTimestamp UpdatedAt { get; set; }

    public bool Deleted { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public int Priority { get; set; }

    public int Category { get; set; }

    public DateTimeOffset Due { get; set; }
}
