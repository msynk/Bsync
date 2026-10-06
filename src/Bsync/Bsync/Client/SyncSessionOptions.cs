using Bsync.Conflicts;
using Bsync.Transport;
using Microsoft.Extensions.Logging;

namespace Bsync.Client;

/// <summary>Configuration of a <see cref="SyncSession{TDocument}"/>.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed record SyncSessionOptions<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>Opens the replica for an account. Each account must get its own storage.</summary>
    public required Func<string, CancellationToken, Task<LocalReplica<TDocument>>> OpenReplica { get; init; }

    /// <summary>Creates the transport used for an account (its credentials belong to that account).</summary>
    public required Func<string, ISyncTransport<TDocument>> CreateTransport { get; init; }

    /// <summary>Deep-clone function for documents (trim/AOT safe, for example <c>DocumentCloner.Json</c>).</summary>
    public required Func<TDocument, TDocument> Cloner { get; init; }

    /// <summary>
    /// Takes the replication lease for an account, or returns <see langword="null"/> if another instance (tab)
    /// holds it. When not set, this session always replicates.
    /// </summary>
    public Func<string, CancellationToken, Task<IAsyncDisposable?>>? AcquireLease { get; init; }

    /// <summary>Conflict policy. Default: the engine's default.</summary>
    public IConflictHandler<TDocument>? ConflictHandler { get; init; }

    /// <summary>Engine batch sizes and budgets.</summary>
    public SyncOptions<TDocument>? EngineOptions { get; init; }

    /// <summary>A short host description reported in <see cref="SyncCapabilities.Host"/>. Default <c>local</c>.</summary>
    public string Host { get; init; } = "local";

    /// <summary>Time between syncs when idle. Default 30 seconds.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>First retry delay after a transient failure. Default 1 second.</summary>
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest retry delay. Default 5 minutes.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often a follower (no lease) announces possible changes made by the owner. Default 2 seconds.</summary>
    public TimeSpan FollowerRefresh { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Time source for delays (tests use a fake). Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Listen to the transport's hint stream (<see cref="ISyncTransport{TDocument}.StreamAsync"/>) and sync as soon
    /// as the server announces a change. Hints only shorten the wait: a missed hint delays synchronization until
    /// the next interval, never loses data (I13). Default <see langword="false"/>.
    /// </summary>
    public bool LiveHints { get; init; }

    /// <summary>
    /// Called when a replica opens; returns something to dispose when it closes. Hosts use it to connect platform
    /// events (browser <c>online</c>/visibility, native resume) to <see cref="SyncSession{TDocument}.RequestSync"/>.
    /// </summary>
    public Func<SyncSession<TDocument>, string, CancellationToken, Task<IAsyncDisposable?>>? AttachLifecycle { get; init; }

    /// <summary>
    /// Called once per failure streak when the server answers <c>unauthorized</c>, with the account. Return
    /// <see cref="CredentialRenewal.Renewed"/> to retry at once, <see cref="CredentialRenewal.Offline"/> when the identity
    /// provider could not be reached (the session keeps everything and tries again with backoff; an exception counts as
    /// offline), or <see cref="CredentialRenewal.SignInRequired"/> when only the user can fix it (uploads stop with
    /// <see cref="SyncState.AttentionRequired"/>). Share one renewal between an account's sessions with
    /// <see cref="CredentialRenewals.Coalesce"/>.
    /// </summary>
    public Func<string, CancellationToken, Task<CredentialRenewal>>? RenewCredentials { get; init; }

    /// <summary>
    /// Joins a <see cref="SyncCoordinator"/>: the coordinator decides when this session may sync (concurrency limit,
    /// priority, parents first, shared backoff), listens to hints for it, and includes it in its aggregate status.
    /// </summary>
    public SyncCoordination? Coordination { get; init; }

    /// <summary>
    /// Deletes everything this device holds for an account: database files, blob content, keys (task G3). Called by
    /// <see cref="SyncSession{TDocument}.DeleteReplicaAsync"/> after the account's replica is closed. Must succeed when
    /// there is nothing to delete. Without it, <c>DeleteReplicaAsync</c> throws.
    /// </summary>
    public Func<string, CancellationToken, Task>? DeleteReplica { get; init; }

    /// <summary>
    /// Receives state changes and failures (category <c>Bsync.SyncSession</c>). Messages carry states, counts and
    /// error codes, never document data or account names. The DI recipes use the container's logger factory.
    /// </summary>
    public ILogger? Logger { get; init; }
}
