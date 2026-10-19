using Bsync.Storage;

namespace Bsync.Client;

/// <summary>
/// Validated composition for lifecycle coordination and foreground/headless resource scopes. Catalog and locks must
/// live outside deletable resources. Register every participant; lower-level APIs cannot detect unregistered users.
/// Resource factories must use only the supplied account and must close partially opened resources before throwing.
/// </summary>
public sealed class AccountComposition
{
    private readonly IOrchestrationStore _catalog;
    private readonly IAccountResourceLock _locks;
    private readonly List<Registration> _resources = [];
    private bool _sealed;

    /// <summary>Requires durable deletion state and non-expiring physical lifetime ownership.</summary>
    public AccountComposition(IOrchestrationStore catalog, IAccountResourceLock locks)
    { _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); _locks = locks ?? throw new ArgumentNullException(nameof(locks)); }

    /// <summary>
    /// Registers a stable named resource and optional fresh-scope factory. Key resources are deleted after all data
    /// resources. Pending inspection may return unknown, which conservatively blocks default deletion.
    /// Factories are opened in registration order and joined in reverse order. Registration ends on first use.
    /// </summary>
    public AccountComposition Register(AccountResource resource, Func<AccountScope, CancellationToken, Task<IAsyncDisposable>>? openAsync = null, bool keyResource = false)
    {
        if (_sealed) throw new InvalidOperationException("Account composition is already in use.");
        ArgumentNullException.ThrowIfNull(resource);
        OrchestrationValidation.Key(resource.Name);
        if (resource.Name.Length > 128) throw new ArgumentException("Resource name exceeds 128 characters.", nameof(resource));
        ArgumentNullException.ThrowIfNull(resource.StopAsync); ArgumentNullException.ThrowIfNull(resource.DeleteAsync);
        if (_resources.Any(r => r.Resource.Name == resource.Name)) throw new ArgumentException("Duplicate account resource name.", nameof(resource));
        _resources.Add(new(resource, openAsync, keyResource));
        return this;
    }

    /// <summary>Creates the existing coordinator with mandatory ownership and validated, data-before-key deletion order.</summary>
    public SyncCoordinator CreateCoordinator(Func<AccountScope, CancellationToken, Task<bool>>? credentials = null,
        Func<AccountScope, CancellationToken, Task>? export = null, TimeProvider? timeProvider = null)
    {
        _sealed = true;
        return new(new SyncCoordinatorOptions
        {
            AccountCatalog = _catalog, AccountResourceLock = _locks,
            AccountResources = _resources.OrderBy(r => r.KeyResource).Select(r => r.Resource).ToArray(),
            CheckNetworkCredentialsAsync = credentials, ExportAccountWork = export, TimeProvider = timeProvider ?? TimeProvider.System,
        });
    }

    /// <summary>
    /// Opens a fresh shared physical lifetime, refuses deletion tombstones, then opens registered factories.
    /// Dispose the scope only after all callers/readers stop; it joins resources before releasing ownership.
    /// It never clears a deletion tombstone or provisions a removed account. Explicit coordinator open handles that policy.
    /// </summary>
    public Task<AccountWorkScope> OpenWorkAsync(AccountScope account, CancellationToken cancellationToken = default) => OpenScopeAsync(account, false, cancellationToken);

    /// <summary>
    /// Opens resources under exclusive account ownership for bounded maintenance. Refuses every live work scope,
    /// another maintainer and deletion. All participants must use this composition's physical lock namespace.
    /// </summary>
    public Task<AccountWorkScope> OpenMaintenanceAsync(AccountScope account, CancellationToken cancellationToken = default) => OpenScopeAsync(account, true, cancellationToken);

    private async Task<AccountWorkScope> OpenScopeAsync(AccountScope account, bool exclusive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account); _sealed = true;
        var access = await _locks.TryAcquireAsync(account, exclusive, cancellationToken).ConfigureAwait(false) ?? throw new AccountResourcesInUseException();
        var scope = new AccountWorkScope(account, access, exclusive);
        try
        {
            if (await _catalog.ReadAsync("deleting/" + account.StorageKey, cancellationToken).ConfigureAwait(false) is not null)
                throw new LocalStoreUnavailableException("deleting", "Account deletion must be resolved before work can open.");
            foreach (var registration in _resources)
                if (registration.Open is { } open)
                    scope.Add(registration.Resource.Name, await open(account, cancellationToken).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            return scope;
        }
        catch { await scope.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>
    /// Applies one cooperative deadline to opening and work, then joins disposal before returning. Noncooperative
    /// callbacks/disposal may overrun; timeout throws cancellation after joining and never abandons an open resource.
    /// </summary>
    public async Task<T> RunAsync<T>(AccountScope account, TimeSpan budget, Func<AccountWorkScope, CancellationToken, Task<T>> work,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var deadline = new CooperativeDeadline(budget, timeProvider ?? TimeProvider.System, cancellationToken, "account_work");
        T result;
        await using (var scope = await OpenWorkAsync(account, deadline.Token).ConfigureAwait(false))
        {
            _ = deadline.Remaining;
            result = await work(scope, deadline.Token).ConfigureAwait(false);
        }
        _ = deadline.Remaining;
        return result;
    }

    private sealed record Registration(AccountResource Resource, Func<AccountScope, CancellationToken, Task<IAsyncDisposable>>? Open, bool KeyResource);

    /// <summary>
    /// Closes the downgrade boundary before enabling protected payloads/v2 encryption. Refuses active account users;
    /// the factory opens the exact account's metadata under exclusive ownership. A shared SQLite database's schema
    /// barrier also prevents older binaries opening its other accounts. There is no automatic downgrade to version 1.
    /// </summary>
    public async Task UpgradeFeatureStoreAsync(AccountScope account,
        Func<AccountScope, CancellationToken, Task<IOrchestrationFeatureStore>> openStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account); ArgumentNullException.ThrowIfNull(openStore); _sealed = true;
        await using var exclusive = await _locks.TryAcquireAsync(account, true, cancellationToken).ConfigureAwait(false) ?? throw new AccountResourcesInUseException();
        if (await _catalog.ReadAsync("deleting/" + account.StorageKey, cancellationToken).ConfigureAwait(false) is not null)
            throw new LocalStoreUnavailableException("deleting", "Complete account deletion before a format upgrade.");
        await using var store = await openStore(account, cancellationToken).ConfigureAwait(false);
        await store.RequireFeatureVersionAsync(2, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A fresh account resource lifetime. Never share this scope across accounts or release it before its readers finish.</summary>
public sealed class AccountWorkScope : IAsyncDisposable
{
    private readonly List<(string Name, IAsyncDisposable Value)> _resources = [];
    private IAsyncDisposable? _access;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _exclusive;
    internal AccountWorkScope(AccountScope account, IAsyncDisposable access, bool exclusive) { Account = account; _access = access; _exclusive = exclusive; }
    internal void RequireMaintenance()
    {
        ObjectDisposedException.ThrowIf(_access is null, this);
        if (!_exclusive) throw new InvalidOperationException("Reconciliation requires an exclusive account maintenance scope.");
    }
    internal void Add(string name, IAsyncDisposable value) { ArgumentNullException.ThrowIfNull(value); _resources.Add((name, value)); }
    /// <summary>The sole account authorized for this local resource scope.</summary>
    public AccountScope Account { get; }
    /// <summary>Returns a registered resource; callers must stop using it before scope disposal.</summary>
    public T Get<T>(string name) where T : class, IAsyncDisposable
    {
        ObjectDisposedException.ThrowIf(_access is null, this);
        return _resources.Single(r => r.Name == name).Value as T ?? throw new InvalidOperationException("Registered resource type mismatch.");
    }
    /// <summary>Joins resources in reverse order. A failing close retains account ownership; retry disposal to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (_resources.Count > 0)
            {
                await _resources[^1].Value.DisposeAsync().ConfigureAwait(false);
                _resources.RemoveAt(_resources.Count - 1);
            }
            if (_access is { } access) await access.DisposeAsync().ConfigureAwait(false);
            _access = null;
        }
        finally { _gate.Release(); }
    }
}
