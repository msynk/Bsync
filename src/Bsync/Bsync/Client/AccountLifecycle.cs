using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Client;

/// <summary>Outcome of opening local account resources. Network credentials are separate from local unlock.</summary>
public enum AccountOpenState
{
    /// <summary>All local replicas are open.</summary>
    Opened,
    /// <summary>Local key access is locked.</summary>
    Locked,
    /// <summary>A required local key is unavailable; data remains intact.</summary>
    KeyUnavailable,
    /// <summary>Storage cannot be opened or its schema is unsupported.</summary>
    StorageUnavailable,
    /// <summary>No account is open, including after completing interrupted deletion.</summary>
    Stopped,
    /// <summary>Local replicas are open, but the host cannot currently supply network credentials.</summary>
    CredentialsRequired,
    /// <summary>The host deferred local opening until a migration can safely complete; existing data remains intact.</summary>
    Migrating,
    /// <summary>Another account lifetime is performing exclusive deletion/migration. Retry after it closes.</summary>
    InUse,
}

/// <summary>Counts read from open replicas and registered resources, not a stale status snapshot. Categories may overlap.</summary>
/// <param name="Pending">Unconfirmed local revisions.</param>
/// <param name="Conflicts">Retained conflicts.</param>
/// <param name="Rejected">Parked protocol rejections.</param>
public sealed record AccountPendingWork(int Pending, int Conflicts, int Rejected)
{
    /// <summary>Pending work reported by registered non-replica resources, such as workflows or transfer queues.</summary>
    public long ResourcePending { get; init; }
    /// <summary>Resources that cannot establish a pending count; default deletion conservatively refuses them.</summary>
    public int UnknownResources { get; init; }
    /// <summary>Whether deletion would discard known work or cannot prove that registered resources are empty.</summary>
    public bool HasWork => Pending != 0 || Conflicts != 0 || Rejected != 0 || ResourcePending != 0 || UnknownResources != 0;
}

/// <summary>Host inspection of additional account work. Known=false preserves uncertainty; Pending must be nonnegative.</summary>
/// <param name="Pending">Count of work requiring preservation or a decision.</param>
/// <param name="Known">Whether the host established the complete count.</param>
public sealed record AccountResourcePendingWork(long Pending, bool Known = true);

/// <summary>Local open result; paused/offline opening does not test server credentials.</summary>
/// <param name="State">Local resource outcome.</param>
/// <param name="Account">Account scope.</param>
/// <param name="PendingWork">Current summary, or null when inaccessible storage makes it unknown.</param>
public sealed record AccountOpenResult(AccountOpenState State, AccountScope Account, AccountPendingWork? PendingWork);

/// <summary>Explicit treatment of local work when deleting an account.</summary>
public enum AccountDeletionPolicy
{
    /// <summary>Refuse deletion if work is pending, conflicted or rejected (default).</summary>
    PreservePending,
    /// <summary>Run the configured durable export before deleting.</summary>
    ExportPending,
    /// <summary>Explicitly discard local work.</summary>
    DiscardPending,
}

/// <summary>Extra account-owned resources, deleted after collection replicas in stable registration order.</summary>
/// <param name="Name">Stable resource identifier, unique across releases.</param>
/// <param name="StopAsync">Stops and joins active work before credentials change or deletion.</param>
/// <param name="DeleteAsync">Idempotently deletes only the supplied account's resources.</param>
public sealed record AccountResource(string Name, Func<AccountScope, CancellationToken, Task> StopAsync, Func<AccountScope, CancellationToken, Task> DeleteAsync)
{
    /// <summary>
    /// Inspects local workflows/transfers/other work without modifying it or requiring network access. Null reports unknown, so PreservePending refuses
    /// deletion. Empty cache/key-only resources may explicitly return (0,true). ExportPending must export all resources.
    /// </summary>
    public Func<AccountScope, CancellationToken, Task<AccountResourcePendingWork>>? InspectPendingAsync { get; init; }
}

/// <summary>Host key provider can throw this from OpenReplica to report locked/unavailable keys without deleting data.</summary>
public sealed class ReplicaKeyUnavailableException : Exception
{
    /// <summary>Creates an explicit key-access failure; do not include key bytes in the message.</summary>
    public ReplicaKeyUnavailableException(bool locked) : base(locked ? "Local key access is locked." : "Local key is unavailable.") => Locked = locked;
    /// <summary>Whether the host can unlock the existing key.</summary>
    public bool Locked { get; }
}

/// <summary>Host replica factories throw this when a migration requires a later retry; never open an empty replacement.</summary>
public sealed class ReplicaMigrationPendingException() : Exception("Local replica migration must complete before opening.");

/// <summary>Replication was refused by the host credential check. Local replicas and pending work remain available.</summary>
public sealed class AccountCredentialsUnavailableException() : Exception("Network credentials are unavailable; local work was retained.");

public sealed partial class SyncCoordinator
{
    private readonly SemaphoreSlim _accountLifecycle = new(1, 1);
    private AccountScope? _account;
    private IAsyncDisposable? _accountAccess;

    /// <summary>Account opened through the coordinator's lifecycle API, or null. Legacy per-session opens are not included.</summary>
    public AccountScope? Account => _account;

    /// <summary>
    /// Stops the previous account, opens every replica locally with networking disabled, and optionally enables replication.
    /// Sessions receive account.StorageKey as their account string. Bind transports to this identity; never mutate credentials
    /// on a shared client until SuspendAccountAsync has completed. A pending deletion is resumed before any replica opens.
    /// </summary>
    public async Task<AccountOpenResult> OpenAccountAsync(AccountScope account, bool enableReplication = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await _accountLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopAccountCoreAsync().ConfigureAwait(false);
            try
            {
                _accountAccess = await AcquireAccountAccessAsync(account, exclusive: false, cancellationToken).ConfigureAwait(false);
                if (_options.AccountCatalog is { } catalog && await catalog.ReadAsync(DeletionKey(account), cancellationToken).ConfigureAwait(false) is { } deletion)
                {
                    var journal = JsonSerializer.Deserialize(deletion.Payload, AccountLifecycleJson.Default.AccountDeletionJournal)!;
                    if (journal.Version == 1 && journal.Resources.SequenceEqual(journal.Completed))
                    {
                        if (!await catalog.TryWriteAsync([new(deletion.Key, deletion.Revision, null)], cancellationToken: cancellationToken).ConfigureAwait(false))
                            throw new InvalidOperationException("Account deletion changed concurrently; retry open.");
                    }
                    else
                    {
                        if (_accountAccess is { } access) { await access.DisposeAsync().ConfigureAwait(false); _accountAccess = null; }
                        await using var exclusive = await AcquireAccountAccessAsync(account, exclusive: true, cancellationToken).ConfigureAwait(false);
                        await CompleteDeletionAsync(account, cancellationToken).ConfigureAwait(false);
                        return new(AccountOpenState.Stopped, account, new(0, 0, 0));
                    }
                }
                foreach (var member in Snapshot())
                {
                    await member.Session.SetReplicationEnabledAsync(false, cancellationToken).ConfigureAwait(false);
                    await member.Session.OpenAsync(account.StorageKey, cancellationToken).ConfigureAwait(false);
                }
                _account = account;
                var work = await PendingWorkCoreAsync(cancellationToken).ConfigureAwait(false);
                if (enableReplication)
                {
                    try { await EnableAccountCoreAsync(cancellationToken).ConfigureAwait(false); }
                    catch (AccountCredentialsUnavailableException) { return new(AccountOpenState.CredentialsRequired, account, work); }
                }
                return new(AccountOpenState.Opened, account, work);
            }
            catch (AccountResourcesInUseException)
            {
                await StopAccountCoreAsync().ConfigureAwait(false);
                return new(AccountOpenState.InUse, account, null);
            }
            catch (ReplicaKeyUnavailableException error)
            {
                await StopAccountCoreAsync().ConfigureAwait(false);
                return new(error.Locked ? AccountOpenState.Locked : AccountOpenState.KeyUnavailable, account, null);
            }
            catch (LocalStoreUnavailableException)
            {
                await StopAccountCoreAsync().ConfigureAwait(false);
                return new(AccountOpenState.StorageUnavailable, account, null);
            }
            catch (ReplicaMigrationPendingException)
            {
                await StopAccountCoreAsync().ConfigureAwait(false);
                return new(AccountOpenState.Migrating, account, null);
            }
            catch { await StopAccountCoreAsync().ConfigureAwait(false); throw; }
        }
        finally { _accountLifecycle.Release(); }
    }

    /// <summary>Cancels and joins network and registered resource work while preserving local replicas and pending work.</summary>
    public async Task SuspendAccountAsync(CancellationToken cancellationToken = default)
    {
        await _accountLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SuspendAccountCoreAsync().ConfigureAwait(false); }
        finally { _accountLifecycle.Release(); }
    }

    /// <summary>Enables replication after the host credential check. Unavailable credentials throw AccountCredentialsUnavailableException and preserve local access.</summary>
    public async Task ResumeAccountAsync(CancellationToken cancellationToken = default)
    {
        await _accountLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_account is null) throw new InvalidOperationException("No account is open.");
            await EnableAccountCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _accountLifecycle.Release(); }
    }

    /// <summary>Stops work and closes the replicas without discarding data. Idempotent.</summary>
    public async Task CloseAccountAsync(CancellationToken cancellationToken = default)
    {
        await _accountLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopAccountCoreAsync().ConfigureAwait(false); }
        finally { _accountLifecycle.Release(); }
    }

    /// <summary>Reads current local pending work. Hosts should suspend first when they need a settled network state.</summary>
    public async Task<AccountPendingWork> GetPendingWorkAsync(CancellationToken cancellationToken = default)
    {
        await _accountLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await PendingWorkCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _accountLifecycle.Release(); }
    }

    /// <summary>
    /// Deletes an account using a durable journal in AccountCatalog. Open the account before starting deletion so pending
    /// work can be checked. Interrupted deletion can be retried without reopening. Returns false when PreservePending refuses.
    /// Hosts must quiesce UI writes before deletion/export. Resource names are journaled; changing them mid-deletion is refused.
    /// With AccountResourceLock configured, deletion closes this account first, refuses other open lifetimes, then reopens
    /// locally under exclusive access to recheck pending work. Even a refused deletion leaves this coordinator closed.
    /// </summary>
    public async Task<bool> DeleteAccountAsync(AccountScope account, AccountDeletionPolicy policy = AccountDeletionPolicy.PreservePending,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        var catalog = _options.AccountCatalog ?? throw new InvalidOperationException("Account deletion requires a durable AccountCatalog outside the deleted resources.");
        await _accountLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_options.AccountResourceLock is not null)
                return await DeleteWithResourceLockAsync(account, policy, cancellationToken).ConfigureAwait(false);
            if (await catalog.ReadAsync(DeletionKey(account), cancellationToken).ConfigureAwait(false) is null)
            {
                if (_account != account) throw new InvalidOperationException("Open the account before starting deletion to inspect pending work.");
                if (Snapshot().Any(m => !m.Session.CanDeleteReplica)) throw new InvalidOperationException("Every collection must provide DeleteReplica.");
                var resources = ResourceNames();
                await SuspendAccountCoreAsync().ConfigureAwait(false);
                var pending = await PendingWorkCoreAsync(cancellationToken).ConfigureAwait(false);
                if (pending.HasWork && policy == AccountDeletionPolicy.PreservePending) return false;
                if (policy == AccountDeletionPolicy.ExportPending)
                {
                    var export = _options.ExportAccountWork ?? throw new InvalidOperationException("Configure a durable ExportAccountWork callback.");
                    await export(account, cancellationToken).ConfigureAwait(false);
                }
                var journal = new AccountDeletionJournal(1, resources, []);
                if (!await catalog.TryWriteAsync([new(DeletionKey(account), null, JsonSerializer.Serialize(journal, AccountLifecycleJson.Default.AccountDeletionJournal))],
                    cancellationToken: cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Another owner started account deletion; retry.");
            }
            await StopAccountCoreAsync().ConfigureAwait(false);
            await CompleteDeletionAsync(account, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _accountLifecycle.Release(); }
    }

    private static string DeletionKey(AccountScope account) => "deleting/" + account.StorageKey;

    private async ValueTask<IAsyncDisposable?> AcquireAccountAccessAsync(AccountScope account, bool exclusive, CancellationToken token)
    {
        if (_options.AccountResourceLock is not { } provider) return null;
        return await provider.TryAcquireAsync(account, exclusive, token).ConfigureAwait(false) ?? throw new AccountResourcesInUseException();
    }

    private async Task<bool> DeleteWithResourceLockAsync(AccountScope account, AccountDeletionPolicy policy, CancellationToken token)
    {
        var wasOpen = _account == account;
        await StopAccountCoreAsync().ConfigureAwait(false);
        await using var exclusive = await AcquireAccountAccessAsync(account, exclusive: true, token).ConfigureAwait(false);
        try
        {
            var catalog = _options.AccountCatalog!;
            if (await catalog.ReadAsync(DeletionKey(account), token).ConfigureAwait(false) is null)
            {
                if (!wasOpen) throw new InvalidOperationException("Open the account before starting deletion to inspect pending work.");
                if (Snapshot().Any(m => !m.Session.CanDeleteReplica)) throw new InvalidOperationException("Every collection must provide DeleteReplica.");
                var resources = ResourceNames();
                // Another shared owner could have written during the close/exclusive-acquire gap. Re-read the real
                // replicas under exclusive ownership rather than trusting the earlier status or pending count.
                foreach (var member in Snapshot())
                {
                    await member.Session.SetReplicationEnabledAsync(false, token).ConfigureAwait(false);
                    await member.Session.OpenAsync(account.StorageKey, token).ConfigureAwait(false);
                }
                _account = account;
                var pending = await PendingWorkCoreAsync(token).ConfigureAwait(false);
                if (pending.HasWork && policy == AccountDeletionPolicy.PreservePending) return false;
                if (policy == AccountDeletionPolicy.ExportPending)
                {
                    var export = _options.ExportAccountWork ?? throw new InvalidOperationException("Configure a durable ExportAccountWork callback.");
                    await export(account, token).ConfigureAwait(false);
                }
                var journal = new AccountDeletionJournal(1, resources, []);
                if (!await catalog.TryWriteAsync([new(DeletionKey(account), null, JsonSerializer.Serialize(journal, AccountLifecycleJson.Default.AccountDeletionJournal))],
                    cancellationToken: token).ConfigureAwait(false)) throw new InvalidOperationException("Account deletion changed concurrently; retry.");
            }
            await StopAccountCoreAsync().ConfigureAwait(false);
            await CompleteDeletionAsync(account, token).ConfigureAwait(false);
            return true;
        }
        finally { await StopAccountCoreAsync().ConfigureAwait(false); }
    }

    private string[] ResourceNames()
    {
        var names = Snapshot().Select(m => "collection/" + m.Coordination.Name)
            .Concat(_options.AccountResources.Select(r => "resource/" + r.Name)).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidOperationException("Account resource names must be unique.");
        return names;
    }

    private async Task CompleteDeletionAsync(AccountScope account, CancellationToken token)
    {
        var catalog = _options.AccountCatalog!;
        var record = await catalog.ReadAsync(DeletionKey(account), token).ConfigureAwait(false);
        if (record is null) return;
        var journal = JsonSerializer.Deserialize(record.Payload, AccountLifecycleJson.Default.AccountDeletionJournal)!;
        if (journal.Version != 1 || !journal.Resources.SequenceEqual(ResourceNames()))
            throw new InvalidOperationException("Deletion schema/resource registration changed; retain the journal and restore the registered resources.");
        foreach (var name in journal.Resources.Except(journal.Completed, StringComparer.Ordinal))
        {
            if (name.StartsWith("collection/", StringComparison.Ordinal))
                await Snapshot().Single(m => "collection/" + m.Coordination.Name == name).Session.DeleteReplicaAsync(account.StorageKey, token).ConfigureAwait(false);
            else
            {
                var resource = _options.AccountResources.Single(r => "resource/" + r.Name == name);
                await resource.StopAsync(account, token).ConfigureAwait(false);
                await resource.DeleteAsync(account, token).ConfigureAwait(false);
            }
            journal = journal with { Completed = [.. journal.Completed, name] };
            if (!await catalog.TryWriteAsync([new(record.Key, record.Revision, JsonSerializer.Serialize(journal, AccountLifecycleJson.Default.AccountDeletionJournal))],
                cancellationToken: token).ConfigureAwait(false)) throw new InvalidOperationException("Deletion journal changed concurrently; retry.");
            record = (await catalog.ReadAsync(record.Key, token).ConfigureAwait(false))!;
        }
        // Keep a completed tombstone until explicit open, so repeated deletes remain idempotent after process restart.
    }

    private async Task<AccountPendingWork> PendingWorkCoreAsync(CancellationToken token)
    {
        var summaries = await Task.WhenAll(Snapshot().Select(m => m.Session.PendingWorkAsync(token))).ConfigureAwait(false);
        long resourcePending = 0; var unknown = 0;
        if (_account is { } account)
        {
            foreach (var resource in _options.AccountResources)
            {
                if (resource.InspectPendingAsync is not { } inspect) { unknown++; continue; }
                var work = await inspect(account, token).ConfigureAwait(false);
                if (work.Pending < 0) throw new InvalidOperationException("A resource reported an invalid pending-work count.");
                resourcePending = checked(resourcePending + work.Pending);
                if (!work.Known) unknown++;
            }
        }
        return new(summaries.Sum(s => s.Pending), summaries.Sum(s => s.Conflicts), summaries.Sum(s => s.Rejected))
            { ResourcePending = resourcePending, UnknownResources = unknown };
    }

    private async Task EnableAccountCoreAsync(CancellationToken token)
    {
        if (_account is null) throw new InvalidOperationException("No account is open.");
        // Quiesce any previous network work before the host examines/replaces credentials for this exact account.
        await SuspendAccountCoreAsync().ConfigureAwait(false);
        if (_options.CheckNetworkCredentialsAsync is { } credentials && !await credentials(_account, token).ConfigureAwait(false))
            throw new AccountCredentialsUnavailableException();
        foreach (var member in Snapshot()) await member.Session.SetReplicationEnabledAsync(true, token).ConfigureAwait(false);
    }

    private async Task SuspendAccountCoreAsync()
    {
        foreach (var member in Snapshot()) await member.Session.SetReplicationEnabledAsync(false, CancellationToken.None).ConfigureAwait(false);
        Task? hints;
        lock (_gate) { _hintsStopping?.Cancel(); hints = _hints; _hints = null; _hintAccount = null; }
        if (hints is not null) { try { await hints.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (_account is { } account)
            foreach (var resource in _options.AccountResources) await resource.StopAsync(account, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task StopAccountCoreAsync()
    {
        await SuspendAccountCoreAsync().ConfigureAwait(false);
        foreach (var member in Snapshot()) await member.Session.StopAsync().ConfigureAwait(false);
        _account = null;
        if (_accountAccess is { } access) { await access.DisposeAsync().ConfigureAwait(false); _accountAccess = null; }
    }
}

internal sealed record AccountDeletionJournal(int Version, string[] Resources, string[] Completed);
[JsonSerializable(typeof(AccountDeletionJournal))]
internal partial class AccountLifecycleJson : JsonSerializerContext;
