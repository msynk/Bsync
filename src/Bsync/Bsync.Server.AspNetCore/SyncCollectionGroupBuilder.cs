using Bsync.Protocol;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bsync.Server.AspNetCore;

/// <summary>
/// Adds collections to a group mapped by <see cref="SyncEndpoints.MapSyncCollections"/>. Every collection shares the
/// group's <see cref="SyncEndpointOptions"/> (scope resolver, schemas, limits) and the conventions put on the returned
/// route group (authorization policy, CORS, rate limiting).
/// </summary>
public sealed class SyncCollectionGroupBuilder
{
    private readonly IEndpointRouteBuilder _group;
    private readonly SyncEndpointOptions _options;
    private readonly Dictionary<string, ISyncCommitNotifier> _notifiers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collections = new(StringComparer.Ordinal);

    internal SyncCollectionGroupBuilder(IEndpointRouteBuilder group, SyncEndpointOptions options)
    {
        _group = group;
        _options = options;
    }

    /// <summary>The collections whose authorities announce commits, for the group's multiplexed hint stream.</summary>
    internal IReadOnlyDictionary<string, ISyncCommitNotifier> Notifiers => _notifiers;

    internal Dictionary<string, SyncEndpoints.BatchPull> Pulls { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds <paramref name="collection"/> served by <paramref name="authority"/>.</summary>
    /// <exception cref="ArgumentException">The collection was already added to this group.</exception>
    public SyncCollectionGroupBuilder Add<TDocument>(string collection, ISyncAuthority<TDocument> authority, SyncJsonTypes<TDocument> json)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!_collections.Add(collection))
        {
            throw new ArgumentException($"The collection '{collection}' is already mapped in this group.", nameof(collection));
        }

        SyncEndpoints.MapCollection(_group, collection, authority, json, _options, prefix: string.Empty, multiplexedHints: true, batch: Pulls);
        if (authority is ISyncCommitNotifier notifier)
        {
            _notifiers[collection] = notifier;
        }

        return this;
    }

    /// <summary>
    /// Adds <paramref name="collection"/> served by the <see cref="ISyncAuthority{TDocument}"/> and
    /// <see cref="SyncJsonTypes{TDocument}"/> registered in the application's services.
    /// </summary>
    public SyncCollectionGroupBuilder Add<TDocument>(string collection)
        where TDocument : class, ISyncEntity =>
        Add(
            collection,
            _group.ServiceProvider.GetRequiredService<ISyncAuthority<TDocument>>(),
            _group.ServiceProvider.GetRequiredService<SyncJsonTypes<TDocument>>());
}
