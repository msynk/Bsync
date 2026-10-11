using System.Collections.Concurrent;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;

namespace Bsync.Blazor;

/// <summary>
/// A collection for server-connected rendering (Interactive Server, prerendering, static SSR): it reads and
/// writes the authority in-process as the current user, through the same authorization as HTTP clients. There
/// is no local replica, so nothing works offline, and every successful write is already accepted by the server.
/// </summary>
/// <remarks>
/// <para>
/// Register it per circuit or request (scoped). A write uses as its base the version this instance last read
/// for the document; a document it has not read is written as new, so it can never silently overwrite a
/// version the user did not see. A conflict returns <see cref="SyncConfirmation.Conflict"/>; reading again
/// picks up the new version.
/// </para>
/// <para>
/// Queries scan the authority's list in pages up to <see cref="ScanLimit"/> documents and throw
/// <see cref="NotSupportedException"/> beyond it, rather than returning a silently truncated result.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class ServerSyncCollection<TDocument> : ISyncCollection<TDocument>, IDisposable
    where TDocument : class, ISyncEntity
{
    /// <summary>The largest collection a query will scan.</summary>
    public const int ScanLimit = 10_000;

    private readonly ISyncAuthority<TDocument> _authority;
    private readonly ISyncDocumentReader<TDocument> _reader;
    private readonly Func<CancellationToken, Task<SyncCallContext>> _context;
    private readonly HybridLogicalClock _clock;
    private readonly Func<TDocument, TDocument> _clone;
    private readonly ISyncCommitNotifier? _notifier;
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);
    private readonly List<Action> _subscribers = [];
    private string? _scope;

    /// <summary>Creates the collection.</summary>
    /// <param name="authority">The authority; it must also implement <see cref="ISyncDocumentReader{TDocument}"/>.</param>
    /// <param name="context">Returns the caller's context (principal and scope) from authenticated state.</param>
    /// <param name="clock">The server's clock for stamping writes made on behalf of users.</param>
    /// <param name="cloner">Deep-clone function.</param>
    public ServerSyncCollection(
        ISyncAuthority<TDocument> authority,
        Func<CancellationToken, Task<SyncCallContext>> context,
        HybridLogicalClock clock,
        Func<TDocument, TDocument> cloner)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cloner);
        _authority = authority;
        _reader = authority as ISyncDocumentReader<TDocument>
            ?? throw new ArgumentException("The authority must support direct reads (ISyncDocumentReader).", nameof(authority));
        _context = context;
        _clock = clock;
        _clone = cloner;
        _notifier = authority as ISyncCommitNotifier;
        if (_notifier is not null)
        {
            _notifier.Committed += OnCommitted;
        }

        Capabilities = new SyncCapabilities("server", DurableOfflineWrites: false, WritesConfirmedByServer: true, LiveUpdates: _notifier is not null);
    }

    /// <inheritdoc />
    public SyncCapabilities Capabilities { get; }

    /// <inheritdoc />
    public SyncStatus Status { get; } = new(SyncState.Synced, 0, null, null);

    /// <inheritdoc />
    public async Task<TDocument?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var context = await ContextAsync(cancellationToken).ConfigureAwait(false);
        var stored = await _reader.GetAsync(context, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        _versions[id] = stored.Version;
        return stored.Document.Deleted ? null : stored.Document;
    }

    /// <inheritdoc />
    public async Task<SyncItemStatus?> GetItemStatusAsync(string id, CancellationToken cancellationToken = default) =>
        await GetAsync(id, cancellationToken).ConfigureAwait(false) is null ? null : new SyncItemStatus(SyncItemState.Synced);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TDocument>> QueryAsync(SyncQuery<TDocument>? query = null, CancellationToken cancellationToken = default)
    {
        var context = await ContextAsync(cancellationToken).ConfigureAwait(false);
        var documents = new List<TDocument>();
        string? after = null;
        while (true)
        {
            var page = await _reader.ListAsync(context, 500, after, cancellationToken).ConfigureAwait(false);
            foreach (var stored in page)
            {
                _versions[stored.Document.Id] = stored.Version;
                documents.Add(stored.Document);
            }

            if (page.Count == 0)
            {
                break;
            }

            if (documents.Count > ScanLimit)
            {
                throw new NotSupportedException($"The collection has more than {ScanLimit} documents; use a database-backed query for it.");
            }

            after = page[^1].Document.Id;
        }

        return (query ?? new SyncQuery<TDocument>()).Apply(documents);
    }

    /// <inheritdoc />
    public async Task<SyncWriteResult> SaveAsync(TDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        SyncIds.Validate(document.Id, nameof(document));
        var copy = _clone(document);
        copy.UpdatedAt = _clock.Now();
        return await PushAsync(copy, _versions.TryGetValue(copy.Id, out var version) ? version : null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each document is based on the version this instance last read, as with <see cref="SaveAsync"/>. If any of them
    /// conflicts or is refused, none is written: that document reports its own result and the others report
    /// <see cref="SyncConfirmation.Conflict"/> with a message naming the group.
    /// </remarks>
    public async Task<IReadOnlyList<SyncWriteResult>> SaveAllAsync(IReadOnlyList<TDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0 || documents.Select(static d => d.Id).Distinct(StringComparer.Ordinal).Count() != documents.Count)
        {
            throw new ArgumentException("The group must contain at least one document and each id once.", nameof(documents));
        }

        var group = Guid.CreateVersion7().ToString("N");
        var operations = documents.Select(document =>
        {
            SyncIds.Validate(document.Id, nameof(documents));
            var copy = _clone(document);
            copy.UpdatedAt = _clock.Now();
            return new PushOperation<TDocument>(Guid.CreateVersion7().ToString("N"), copy.Id, _versions.TryGetValue(copy.Id, out var version) ? version : null, copy)
            {
                Group = group,
                GroupSize = documents.Count,
            };
        }).ToList();
        var context = await ContextAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = (await _authority.PushAsync(context, new PushRequest<TDocument>(operations), cancellationToken).ConfigureAwait(false)).Outcomes;
        return [.. operations.Zip(outcomes).Select(pair => Result(pair.First.DocumentId, pair.Second))];
    }

    /// <inheritdoc />
    public async Task<SyncWriteResult> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var context = await ContextAsync(cancellationToken).ConfigureAwait(false);
        var stored = await _reader.GetAsync(context, id, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Document.Deleted)
        {
            return new SyncWriteResult(id, SyncConfirmation.NotFound);
        }

        var tombstone = _clone(stored.Document);
        tombstone.Deleted = true;
        tombstone.UpdatedAt = _clock.Now();

        // Base on the version the user saw; fall back to the one just read only if this instance never read it.
        var baseVersion = _versions.TryGetValue(id, out var seen) ? seen : stored.Version;
        return await PushAsync(tombstone, baseVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Always empty: without a local replica a conflicting write returns <see cref="SyncConfirmation.Conflict"/> at once.</remarks>
    public Task<IReadOnlyList<SyncDocumentConflict<TDocument>>> GetConflictsAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SyncDocumentConflict<TDocument>>>([]);

    /// <inheritdoc />
    /// <remarks>Reached at once: writes are confirmed by the server before they return, and reads go to the server.</remarks>
    public Task<SyncGoalResult> SyncAsync(SyncGoal goal, TimeSpan budget, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncGoalResult(true, true, 0, [], default));

    /// <inheritdoc />
    /// <remarks>Always empty: without a local replica nothing waits for a decision.</remarks>
    public Task<SyncIssuePage> GetIssuesAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncIssuePage([], 0));

    /// <inheritdoc />
    public Task<SyncWriteResult> ResolveConflictAsync(string id, TDocument resolved, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncWriteResult(id, SyncConfirmation.NotFound));

    /// <inheritdoc />
    public Task<bool> DiscardConflictAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <inheritdoc />
    /// <remarks>Always <see cref="SyncConfirmation.NotFound"/>: a refused server-connected write is reported at once and nothing is kept.</remarks>
    public Task<SyncWriteResult> RetryAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncWriteResult(id, SyncConfirmation.NotFound));

    /// <inheritdoc />
    /// <remarks>Always <see langword="false"/>: there are no unsynchronized changes.</remarks>
    public Task<bool> RevertAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <inheritdoc />
    public IDisposable Subscribe(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        lock (_subscribers)
        {
            _subscribers.Add(onChanged);
        }

        return new Unsubscriber(() =>
        {
            lock (_subscribers)
            {
                _subscribers.Remove(onChanged);
            }
        });
    }

    /// <summary>Stops listening to the authority (called when the circuit or request scope ends).</summary>
    public void Dispose()
    {
        if (_notifier is not null)
        {
            _notifier.Committed -= OnCommitted;
        }

        lock (_subscribers)
        {
            _subscribers.Clear();
        }
    }

    /// <summary>The number of active subscriptions (diagnostics).</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_subscribers)
            {
                return _subscribers.Count;
            }
        }
    }

    private async Task<SyncWriteResult> PushAsync(TDocument document, long? baseVersion, CancellationToken cancellationToken)
    {
        var context = await ContextAsync(cancellationToken).ConfigureAwait(false);
        var operation = new PushOperation<TDocument>(Guid.CreateVersion7().ToString("N"), document.Id, baseVersion, document);
        var outcome = (await _authority.PushAsync(context, new PushRequest<TDocument>([operation]), cancellationToken).ConfigureAwait(false)).Outcomes[0];
        return Result(document.Id, outcome);
    }

    private SyncWriteResult Result(string id, PushOutcome<TDocument> outcome)
    {
        switch (outcome.Kind)
        {
            case PushOutcomeKind.Accepted:
                _versions[id] = outcome.Version!.Value;
                return new SyncWriteResult(id, SyncConfirmation.AcceptedByServer);
            case PushOutcomeKind.Conflict:
                _versions.TryRemove(id, out _);
                return new SyncWriteResult(id, SyncConfirmation.Conflict, "The document was changed by someone else. Reload it and try again.");
            case PushOutcomeKind.RetryLater when outcome.ErrorCode == PushErrorCodes.GroupAborted:
                return new SyncWriteResult(id, SyncConfirmation.Conflict, "Not saved: another document of the same group could not be saved.");
            default:
                return new SyncWriteResult(id, SyncConfirmation.Rejected, outcome.ErrorCode);
        }
    }

    private async Task<SyncCallContext> ContextAsync(CancellationToken cancellationToken)
    {
        var context = await _context(cancellationToken).ConfigureAwait(false);
        _scope = context.Scope;
        return context;
    }

    private void OnCommitted(AuthorityCommit commit)
    {
        // Only this user's scope; a hint to re-query, never a data carrier.
        if (_scope is null || !string.Equals(commit.Scope, _scope, StringComparison.Ordinal))
        {
            return;
        }

        Action[] subscribers;
        lock (_subscribers)
        {
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber();
            }
            catch
            {
                // A failing component must not affect other users' writes.
            }
        }
    }
}
