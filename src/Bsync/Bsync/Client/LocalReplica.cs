using Bsync.Conflicts;
using Bsync.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bsync.Client;

/// <summary>A local replica opened for one account: its store and the HLC node id to stamp writes with.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
/// <param name="Store">The durable local store (disposed by the session if it implements <see cref="IAsyncDisposable"/>).</param>
/// <param name="NodeId">A node id unique to this replica incarnation (for example <see cref="ReplicaIdentity.Incarnation"/>).</param>
public sealed record LocalReplica<TDocument>(ILocalStore<TDocument> Store, string NodeId)
    where TDocument : class, ISyncEntity
{
    /// <summary>
    /// Whether the platform protects the replica's storage from eviction (a browser's persistent storage), or
    /// <see langword="null"/> when unknown or not applicable. Reported in <see cref="SyncStatus.PersistentStorage"/>.
    /// </summary>
    public bool? PersistentStorage { get; init; }
}
