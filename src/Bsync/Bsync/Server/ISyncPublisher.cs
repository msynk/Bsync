using System.Data.Common;

namespace Bsync.Server;

/// <summary>
/// Puts server-originated changes into the feed (ADR-014): API endpoints, background jobs, imports and projection
/// rebuilds. Writes are versioned under the same feed lock as replicated writes, with no operation ids or base versions,
/// so a replica's concurrent edit of the same document gets a real conflict.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unchanged content is not republished.</b> A document whose JSON equals the stored one (ignoring
/// <see cref="ISyncEntity.UpdatedAt"/>) keeps its version, so rebuilding a whole projection adds no feed entries for
/// documents that did not change. Serialization must be deterministic: members of dictionaries and extension data are
/// compared in insertion order.
/// </para>
/// <para>
/// A document with a default <see cref="ISyncEntity.UpdatedAt"/> is stamped with the authority's clock; otherwise its
/// timestamp is kept.
/// </para>
/// <para>
/// <b>Transactions.</b> With a <see cref="DbTransaction"/>, the write enlists in the caller's transaction (for example the
/// one EF Core uses for <c>SaveChanges</c>) and the caller commits or rolls back both. Authorities without a database
/// accept only <see langword="null"/>.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public interface ISyncPublisher<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>Creates or replaces <paramref name="document"/> in <paramref name="scope"/>, unless its content is unchanged.</summary>
    Task<SyncPublishResult> UpsertAsync(string scope, TDocument document, DbTransaction? transaction = null, CancellationToken cancellationToken = default);

    /// <summary>Replaces <paramref name="id"/> in <paramref name="scope"/> with a tombstone, unless it is absent or already deleted.</summary>
    Task<SyncPublishResult> DeleteAsync(string scope, string id, DbTransaction? transaction = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="scope"/> contain exactly <paramref name="documents"/>: changed and new ones are written,
    /// unchanged ones are left alone, and documents that are no longer listed become tombstones (replicas remove them
    /// through the feed, without a reset).
    /// </summary>
    Task<SyncPublishResult> ReplaceScopeAsync(string scope, IEnumerable<TDocument> documents, DbTransaction? transaction = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes copies of a read-only projection to several scopes (fan-out) in one batch. Use it only for documents that
    /// replicas never write: writable documents shared by several principals belong in one shared scope (ADR-014).
    /// </summary>
    Task<SyncPublishResult> PublishAsync(TDocument document, IEnumerable<string> scopes, DbTransaction? transaction = null, CancellationToken cancellationToken = default);
}
