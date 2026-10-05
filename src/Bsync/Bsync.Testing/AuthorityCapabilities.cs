namespace Bsync.Testing;

/// <summary>
/// Optional authority behaviour a driver can exercise. Cases that need a capability the driver does not declare are
/// not run (<see cref="AuthorityConformance.CasesFor"/>); the core cases run against every authority.
/// </summary>
[Flags]
public enum AuthorityCapabilities
{
    /// <summary>Only the core cases.</summary>
    None = 0,

    /// <summary>The authority advertises <c>groups</c> and applies dependency groups all-or-nothing (protocol §4.1).</summary>
    Groups = 1,

    /// <summary>Different <see cref="Server.SyncCallContext.Scope"/> values have isolated feeds, versions and receipts (I07).</summary>
    ScopeIsolation = 2,

    /// <summary>The driver can purge tombstones and receipts (<see cref="AuthorityUnderTest.PurgeTombstonesAsync"/>, <see cref="AuthorityUnderTest.PurgeReceiptsAsync"/>).</summary>
    Retention = 4,

    /// <summary>The driver can start a new epoch with a version floor, as after a restore (<see cref="AuthorityUnderTest.BeginNewEpochAsync"/>).</summary>
    NewEpoch = 8,

    /// <summary>Everything above.</summary>
    All = Groups | ScopeIsolation | Retention | NewEpoch,
}
