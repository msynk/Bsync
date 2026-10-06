using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;

namespace Bsync.Testing;

/// <summary>
/// How a case wants the authority under test configured. A driver must apply every member it is given; members
/// left <see langword="null"/> mean "no hook".
/// </summary>
public sealed class AuthorityConformanceOptions
{
    /// <summary>The physical clock the authority validates origin timestamps against.</summary>
    public required IPhysicalClock Clock { get; init; }

    /// <summary>How far an origin timestamp may be ahead of <see cref="Clock"/>. Default five minutes.</summary>
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Application validation: an error code rejects the operation permanently.</summary>
    public Func<SyncCallContext, PushOperation<ConformanceDocument>, ConformanceDocument?, string?>? Validator { get; init; }

    /// <summary>Read authorization: documents the caller may not read are withheld.</summary>
    public Func<SyncCallContext, ConformanceDocument, bool>? CanRead { get; init; }

    /// <summary>A fingerprint of what the caller may see, bound into checkpoints.</summary>
    public Func<SyncCallContext, string>? ScopeFingerprint { get; init; }

    /// <summary>Read membership: who may read a document (ADR-015). Set together with <see cref="PrincipalKey"/>.</summary>
    public Func<ConformanceDocument, IEnumerable<string>>? Readers { get; init; }

    /// <summary>The caller's principal key for <see cref="Readers"/>.</summary>
    public Func<SyncCallContext, string?>? PrincipalKey { get; init; }
}
