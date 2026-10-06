using System.Text.Json.Serialization.Metadata;
using Bsync.Client;
using Bsync.Documents;
using Bsync.Storage;
using Bsync.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Bsync.Blazor.IndexedDb;

/// <summary>The browser (Blazor WebAssembly) registration recipe.</summary>
public static class IndexedDbSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="ISyncCollection{TDocument}"/> backed by an IndexedDB replica per signed-in account,
    /// replicated by the one tab that holds the Web Locks lease. Call it in the WebAssembly client's
    /// <c>Program.cs</c> only; the replica opens on first use, after the runtime is interactive.
    /// </summary>
    /// <param name="services">The WebAssembly client's services.</param>
    /// <param name="collection">The collection name (also used in the lease name).</param>
    /// <param name="documentType">Source-generated JSON metadata for the document type.</param>
    /// <param name="transport">Creates the transport for an account (for example an <c>HttpSyncTransport</c> with that account's credentials).</param>
    /// <param name="resolveAccount">
    /// Returns the signed-in account id; each account gets its own database (<c>bsync-{account}</c>).
    /// Default: <c>"default"</c> (single-user apps).
    /// </param>
    /// <param name="configure">Optional changes to intervals, backoff or conflict policy.</param>
    /// <param name="indexes">Secondary indexes the replica maintains for <see cref="SyncQuery{TDocument}.Index"/> queries (ADR-018).</param>
    public static IServiceCollection AddBrowserSyncCollection<TDocument>(
        this IServiceCollection services,
        string collection,
        JsonTypeInfo<TDocument> documentType,
        Func<IServiceProvider, string, ISyncTransport<TDocument>> transport,
        Func<IServiceProvider, CancellationToken, Task<string>>? resolveAccount = null,
        Func<SyncSessionOptions<TDocument>, SyncSessionOptions<TDocument>>? configure = null,
        IEnumerable<SyncIndex<TDocument>>? indexes = null)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(documentType);
        ArgumentNullException.ThrowIfNull(transport);
        if (!SyncIds.IsValid(collection))
        {
            throw new ArgumentException("The collection name must be a valid identifier.", nameof(collection));
        }

        var declared = LocalStoreIndexing.Validate(indexes);
        return services.AddLocalSyncCollection<TDocument>(
            sp =>
            {
                var js = sp.GetRequiredService<IJSRuntime>();
                var options = new SyncSessionOptions<TDocument>
                {
                    Host = "browser",
                    Cloner = DocumentCloner.Json(documentType),
                    CreateTransport = account => transport(sp, account),
                    OpenReplica = async (account, cancellationToken) =>
                    {
                        var store = await IndexedDbLocalStore<TDocument>.OpenAsync(
                            js,
                            new IndexedDbStoreOptions { DatabaseName = $"bsync-{account}", Collection = collection },
                            documentType,
                            declared,
                            cancellationToken).ConfigureAwait(false);
                        var identity = await store.GetReplicaIdentityAsync(cancellationToken).ConfigureAwait(false);

                        // Ask the browser not to evict the replica under storage pressure; the answer is reported in
                        // SyncStatus.PersistentStorage (task G3). Browsers may refuse, grant only to installed apps, or
                        // ask the user (Firefox): opening never waits for a person, so an unanswered request is "unknown".
                        bool? persistent;
                        try
                        {
                            persistent = await store.RequestPersistenceAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is TimeoutException or LocalStoreUnavailableException)
                        {
                            persistent = null;
                        }

                        return new LocalReplica<TDocument>(store, identity.Incarnation) { PersistentStorage = persistent };
                    },

                    // Sign-out on a shared device: removes the account's database (every collection in it).
                    DeleteReplica = (account, cancellationToken) => IndexedDbLocalStore<TDocument>.DeleteDatabaseAsync(js, $"bsync-{account}", cancellationToken),
                    AcquireLease = async (account, cancellationToken) =>
                        await IndexedDbReplicaLease.TryAcquireAsync(js, $"bsync-{account}-{collection}", cancellationToken).ConfigureAwait(false),

                    // Sync when the network returns or the tab is shown again, and on server hints when offered.
                    AttachLifecycle = async (session, _, cancellationToken) =>
                        await BrowserLifecycleWatcher.StartAsync(js, session.RequestSync, cancellationToken).ConfigureAwait(false),
                    LiveHints = true,
                };
                return configure is null ? options : configure(options);
            },
            resolveAccount);
    }
}
