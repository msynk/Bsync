using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bsync.Client;

/// <summary>
/// Schedules and observes the sync sessions of many collections (task C3). It is a thin layer over the sessions: each
/// keeps its own engine, store and loop; the coordinator decides when a session may run (a concurrency limit, priorities,
/// parents before children, a shared backoff when the server is unreachable), listens to one multiplexed hint stream
/// for all of them, and reports one aggregate status. Join a session with <see cref="SyncSessionOptions{TDocument}.Coordination"/>.
/// </summary>
public sealed class SyncCoordinator : IAsyncDisposable
{
    private static readonly SyncState[] Severity =
    [
        SyncState.AttentionRequired, SyncState.Offline, SyncState.Syncing, SyncState.Starting, SyncState.Paused,
        SyncState.Follower, SyncState.Synced, SyncState.Stopped,
    ];

    private readonly SyncCoordinatorOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Member> _members = new(StringComparer.Ordinal);
    private readonly List<(int Priority, long Order, TaskCompletionSource Ready)> _waiting = [];
    private readonly CancellationTokenSource _stopping = new();
    private int _running;
    private long _order;
    private DateTimeOffset _unreachableUntil;
    private string? _hintAccount;
    private Task? _hints;
    private CancellationTokenSource? _hintsStopping;
    private bool _hintsFallback;
    private SyncCoordinatorStatus _status = SyncCoordinatorStatus.Empty;

    /// <summary>Creates a coordinator.</summary>
    public SyncCoordinator(SyncCoordinatorOptions? options = null)
    {
        _options = options ?? new SyncCoordinatorOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConcurrency, 1, nameof(options));
        _logger = _options.Logger ?? NullLogger.Instance;
    }

    /// <summary>Raised when the aggregate <see cref="Status"/> changes (not for timestamp-only updates).</summary>
    public event Action? Changed;

    /// <summary>The worst state of all collections, summed counts, and each collection's status.</summary>
    public SyncCoordinatorStatus Status => _status;

    /// <summary>The names of the coordinated collections.</summary>
    public IReadOnlyCollection<string> Collections
    {
        get
        {
            lock (_gate)
            {
                return [.. _members.Keys];
            }
        }
    }

    /// <summary>
    /// Host bridge: asks <paramref name="collection"/> to sync soon, for example when the application's own real-time hub
    /// announces a change. Unknown names are ignored. Hints only speed things up; sessions still reconcile on their
    /// interval (I13).
    /// </summary>
    public void Hint(string collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        Member? member;
        lock (_gate)
        {
            _members.TryGetValue(collection, out member);
        }

        member?.Session.RequestSync();
    }

    /// <summary>
    /// Waits until <paramref name="goal"/> is reached in every collection named in <paramref name="collections"/> (all when
    /// <see langword="null"/>) or <paramref name="budget"/> runs out (task C4). Each collection's replica must be open.
    /// </summary>
    public async Task<SyncGoalResult> SyncAsync(SyncGoal goal, TimeSpan budget, IReadOnlyCollection<string>? collections = null, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var members = Snapshot().Where(m => collections is null || collections.Contains(m.Coordination.Name)).ToList();
        var results = await Task.WhenAll(members.Select(m => m.Session.SyncActiveAsync(goal, budget, progress, cancellationToken))).ConfigureAwait(false);
        return SyncGoalResult.Combine(results);
    }

    /// <summary>Asks every collection to sync soon (for example when the network returns).</summary>
    public void RequestSync()
    {
        foreach (var member in Snapshot())
        {
            member.Session.RequestSync();
        }
    }

    /// <summary>Pauses every collection after its current sync.</summary>
    public void Pause()
    {
        foreach (var member in Snapshot())
        {
            member.Session.Pause();
        }
    }

    /// <summary>Resumes every collection and syncs at once.</summary>
    public void Resume()
    {
        foreach (var member in Snapshot())
        {
            member.Session.Resume();
        }
    }

    /// <summary>Stops the shared hint listener. The sessions are disposed by their owners.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task? hints;
        lock (_gate)
        {
            hints = _hints;
            _hintsStopping?.Cancel();
        }

        if (hints is not null)
        {
            try
            {
                await hints.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Whether the coordinator listens to hints for its sessions (so they must not open their own streams).</summary>
    internal bool MultiplexesHints
    {
        get
        {
            lock (_gate)
            {
                return _options.Hints is not null && !_hintsFallback;
            }
        }
    }

    internal void Attach(ICoordinatedSession session, SyncCoordination coordination)
    {
        lock (_gate)
        {
            if (_members.ContainsKey(coordination.Name))
            {
                throw new ArgumentException($"A collection named '{coordination.Name}' is already coordinated.", nameof(coordination));
            }

            foreach (var parent in coordination.DependsOn ?? [])
            {
                if (!_members.ContainsKey(parent))
                {
                    throw new ArgumentException($"'{coordination.Name}' depends on '{parent}', which must be coordinated first.", nameof(coordination));
                }
            }

            _members[coordination.Name] = new Member(coordination, session);
        }

        session.Changed += Recompute;
        Recompute();
    }

    /// <summary>A coordinated session's replica opened for <paramref name="account"/>: start (or move) the shared hint listener.</summary>
    internal void OnActive(string account)
    {
        if (_options.Hints is not { } hints)
        {
            return;
        }

        lock (_gate)
        {
            if (_hintsFallback || (_hints is not null && _hintAccount == account))
            {
                return;
            }

            _hintsStopping?.Cancel();
            _hintsStopping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _hintAccount = account;
            var stopping = _hintsStopping.Token;
            _hints = Task.Run(() => ListenAsync(hints, account, stopping), CancellationToken.None);
        }
    }

    /// <summary>
    /// Runs one sync of <paramref name="name"/> when a slot is free (highest priority first), after the collections it
    /// depends on uploaded their pending changes, unless another collection just found the server unreachable.
    /// </summary>
    internal async Task<SyncResult> RunAsync(string name, Func<CancellationToken, Task<SyncResult>> sync, CancellationToken cancellationToken)
    {
        Member member;
        lock (_gate)
        {
            member = _members[name];
        }

        if (_options.TimeProvider.GetUtcNow() < _unreachableUntil)
        {
            // Shared backoff: fifteen collections must not each discover that the server is down.
            throw new SyncTransportException(SyncErrorCodes.Unavailable, "The server was unreachable a moment ago; waiting before trying again.", isTransient: true);
        }

        await EnterAsync(member.Coordination.Priority, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var parent in Ancestors(name))
            {
                // Parents push before children (C3): a child created before its parent never reaches the server first.
                if (parent.Session.Status.Pending > 0)
                {
                    try
                    {
                        await parent.Session.SyncNowAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // The parent's own loop reports and retries its failure; the child goes on.
                        _logger.LogDebug(error, "Syncing {Parent} before {Child} failed.", parent.Coordination.Name, name);
                    }
                }
            }

            return await sync(cancellationToken).ConfigureAwait(false);
        }
        catch (SyncTransportException error) when (error.IsTransient)
        {
            _unreachableUntil = _options.TimeProvider.GetUtcNow() + (error.RetryAfter ?? _options.SharedBackoff);
            throw;
        }
        finally
        {
            Leave();
        }
    }

    private Member[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _members.Values];
        }
    }

    /// <summary>Every collection <paramref name="name"/> depends on, directly or not, parents before their children.</summary>
    private List<Member> Ancestors(string name)
    {
        var ordered = new List<Member>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            Visit(name);
        }

        return ordered;

        void Visit(string current)
        {
            foreach (var parent in _members[current].Coordination.DependsOn ?? [])
            {
                if (seen.Add(parent))
                {
                    Visit(parent);
                    ordered.Add(_members[parent]);
                }
            }
        }
    }

    private Task EnterAsync(int priority, CancellationToken cancellationToken)
    {
        TaskCompletionSource ready;
        lock (_gate)
        {
            if (_running < _options.MaxConcurrency && _waiting.Count == 0)
            {
                _running++;
                return Task.CompletedTask;
            }

            ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((priority, _order++, ready));
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _waiting.RemoveAll(w => w.Ready == ready);
            }

            ready.TrySetCanceled(cancellationToken);
        });
        return ready.Task;
    }

    private void Leave()
    {
        lock (_gate)
        {
            if (_waiting.Count == 0)
            {
                _running--;
                return;
            }

            // The slot passes to the highest-priority waiter, oldest first.
            var next = _waiting.OrderByDescending(w => w.Priority).ThenBy(w => w.Order).First();
            _waiting.Remove(next);
            next.Ready.TrySetResult();
        }
    }

    private async Task ListenAsync(Func<string, IReadOnlyList<string>, CancellationToken, IAsyncEnumerable<string>> hints, string account, CancellationToken stopping)
    {
        var failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await foreach (var collection in hints(account, [.. Collections], stopping).ConfigureAwait(false))
                {
                    failures = 0;
                    Hint(collection);
                }

                failures++;
            }
            catch (NotSupportedException)
            {
                // An older server has no multiplexed stream: every session listens on its own, as before.
                lock (_gate)
                {
                    _hintsFallback = true;
                }

                foreach (var member in Snapshot())
                {
                    member.Session.ListenOnOwn();
                }

                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                failures++;
                _logger.LogDebug(error, "The multiplexed hint stream failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30_000, 250 * Math.Pow(2, Math.Min(failures, 8)))), _options.TimeProvider, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Recompute()
    {
        var members = Snapshot();
        var collections = members.ToDictionary(m => m.Coordination.Name, m => m.Session.Status, StringComparer.Ordinal);
        var state = collections.Count == 0
            ? SyncState.Starting
            : collections.Values.Select(s => s.State).OrderBy(s => Array.IndexOf(Severity, s)).First();
        var detail = state is SyncState.AttentionRequired or SyncState.Offline
            ? string.Join("; ", collections.Where(kv => kv.Value.State == state).Select(kv => $"{kv.Key}: {kv.Value.Detail ?? state.ToString()}"))
            : null;
        var next = new SyncCoordinatorStatus(
            state,
            collections.Values.Sum(s => s.Pending),
            collections.Values.Sum(s => s.Conflicts),
            collections.Values.Sum(s => s.Rejected),
            detail,
            collections);

        bool changed;
        lock (_gate)
        {
            changed = !_status.SameAs(next);
            _status = next;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private sealed record Member(SyncCoordination Coordination, ICoordinatedSession Session);
}

/// <summary>Options for <see cref="SyncCoordinator"/>.</summary>
public sealed class SyncCoordinatorOptions
{
    /// <summary>How many collections may sync at the same time. Default 2.</summary>
    public int MaxConcurrency { get; init; } = 2;

    /// <summary>
    /// Opens one hint stream for an account and the given collections, yielding the name of a collection whenever it
    /// changed (for example <see cref="Transport.HttpSyncHints.Multiplexed"/>). Throw <see cref="NotSupportedException"/>
    /// when the server has no such stream: sessions then listen on their own. <see langword="null"/>: no shared stream.
    /// </summary>
    public Func<string, IReadOnlyList<string>, CancellationToken, IAsyncEnumerable<string>>? Hints { get; init; }

    /// <summary>How long other collections wait after one found the server unreachable (unless it said <c>Retry-After</c>). Default 2 seconds.</summary>
    public TimeSpan SharedBackoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Time source (tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Receives coordinator diagnostics.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>How a session joins a <see cref="SyncCoordinator"/>.</summary>
/// <param name="Coordinator">The coordinator.</param>
/// <param name="Name">The collection's name, unique in the coordinator (also the name in multiplexed hints).</param>
/// <param name="Priority">Higher runs first when collections wait for a slot. Default 0.</param>
/// <param name="DependsOn">Collections whose pending changes upload before this one's (parents before children).</param>
public sealed record SyncCoordination(SyncCoordinator Coordinator, string Name, int Priority = 0, IReadOnlyList<string>? DependsOn = null);

/// <summary>The aggregate of a <see cref="SyncCoordinator"/>'s collections.</summary>
/// <param name="State">The most severe state of any collection.</param>
/// <param name="Pending">Local changes not yet accepted, in all collections.</param>
/// <param name="Conflicts">Kept conflicts, in all collections.</param>
/// <param name="Rejected">Parked rejections, in all collections.</param>
/// <param name="Detail">For <see cref="SyncState.AttentionRequired"/> or <see cref="SyncState.Offline"/>: which collections and why.</param>
/// <param name="Collections">Each collection's own status.</param>
public sealed record SyncCoordinatorStatus(SyncState State, int Pending, int Conflicts, int Rejected, string? Detail, IReadOnlyDictionary<string, SyncStatus> Collections)
{
    /// <summary>No collections yet.</summary>
    public static SyncCoordinatorStatus Empty { get; } = new(SyncState.Starting, 0, 0, 0, null, new Dictionary<string, SyncStatus>());

    internal bool SameAs(SyncCoordinatorStatus other) =>
        State == other.State && Pending == other.Pending && Conflicts == other.Conflicts && Rejected == other.Rejected && Detail == other.Detail
        && Collections.Count == other.Collections.Count
        && Collections.All(kv => other.Collections.TryGetValue(kv.Key, out var o) && (kv.Value.State, kv.Value.Pending, kv.Value.Conflicts, kv.Value.Rejected, kv.Value.Detail) == (o.State, o.Pending, o.Conflicts, o.Rejected, o.Detail));
}

/// <summary>What the coordinator needs from a session (implemented by <see cref="SyncSession{TDocument}"/>).</summary>
internal interface ICoordinatedSession
{
    event Action? Changed;

    SyncStatus Status { get; }

    void RequestSync();

    void Pause();

    void Resume();

    /// <summary>Syncs the active replica now, if one is open.</summary>
    Task SyncNowAsync(CancellationToken cancellationToken);

    /// <summary>Starts the session's own hint listener (fallback when the server has no multiplexed stream).</summary>
    void ListenOnOwn();

    /// <summary>Waits for a goal on the open replica.</summary>
    Task<SyncGoalResult> SyncActiveAsync(SyncGoal goal, TimeSpan budget, IProgress<SyncProgress>? progress, CancellationToken cancellationToken);
}
