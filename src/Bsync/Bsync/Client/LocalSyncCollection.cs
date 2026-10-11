namespace Bsync.Client;

/// <summary>
/// A collection backed by a local replica (browser IndexedDB or native SQLite) through a
/// <see cref="SyncSession{TDocument}"/>. Writes are durable locally at once and uploaded in the background.
/// </summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class LocalSyncCollection<TDocument> : ISyncCollection<TDocument>
    where TDocument : class, ISyncEntity
{
    private readonly SyncSession<TDocument> _session;
    private readonly Func<CancellationToken, Task<string>> _resolveAccount;

    /// <summary>Creates the collection.</summary>
    /// <param name="session">The session that owns the replica.</param>
    /// <param name="resolveAccount">
    /// Returns the signed-in account. When it changes, the next call switches the session to that account's
    /// replica.
    /// </param>
    public LocalSyncCollection(SyncSession<TDocument> session, Func<CancellationToken, Task<string>> resolveAccount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(resolveAccount);
        _session = session;
        _resolveAccount = resolveAccount;
        Capabilities = new SyncCapabilities(session.Host, DurableOfflineWrites: !session.PullOnly, WritesConfirmedByServer: false, LiveUpdates: session.LiveHints);
    }

    /// <inheritdoc />
    public SyncCapabilities Capabilities { get; }

    /// <inheritdoc />
    public SyncStatus Status => _session.Status;

    /// <inheritdoc />
    public async Task<TDocument?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        var record = await engine.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return record is { MissingAfterReset: false, Current.Deleted: false } ? record.Current : null;
    }

    /// <inheritdoc />
    public async Task<SyncItemStatus?> GetItemStatusAsync(string id, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        return await engine.GetAsync(id, cancellationToken).ConfigureAwait(false) switch
        {
            null => null,
            { MissingAfterReset: true } => new SyncItemStatus(SyncItemState.MissingAfterReset),
            { Conflict: not null } => new SyncItemStatus(SyncItemState.Conflicted),
            { Rejection: { } rejection } => new SyncItemStatus(SyncItemState.Rejected, rejection.ErrorCode) { Arguments = rejection.Arguments },
            { IsDirty: true } => new SyncItemStatus(SyncItemState.Pending),
            _ => new SyncItemStatus(SyncItemState.Synced),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TDocument>> QueryAsync(SyncQuery<TDocument>? query = null, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        query ??= new SyncQuery<TDocument>();
        Queries.Validate(query);
        if (query.Order is not null)
        {
            return query.Apply(await engine.QueryAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        // Both the default (id) order and an index are store order: walk it in pages and stop at the limit, so memory
        // and time stay bounded by the page, however large the collection is.
        var matches = new List<TDocument>(query.Limit);
        var skip = query.Skip;
        await foreach (var document in WalkAsync(engine, query, cancellationToken).ConfigureAwait(false))
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }

            matches.Add(document);
            if (matches.Count == query.Limit)
            {
                break;
            }
        }

        return matches;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(SyncQuery<TDocument>? query = null, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        query ??= new SyncQuery<TDocument>();
        Queries.Validate(query);
        if (query is { Index: { } index, Where: null })
        {
            return await engine.CountIndexAsync(index, cancellationToken).ConfigureAwait(false);
        }

        if (query.Order is not null)
        {
            return (await engine.QueryAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Count(d => query.Where?.Invoke(d) ?? true);
        }

        var count = 0;
        await foreach (var _ in WalkAsync(engine, query, cancellationToken).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    /// <summary>The matching documents in store order (id, or the query's index), read page by page.</summary>
    private static async IAsyncEnumerable<TDocument> WalkAsync(SyncEngine<TDocument> engine, SyncQuery<TDocument> query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? afterId = null;
        Storage.SyncIndexCursor? afterEntry = null;
        while (true)
        {
            var page = query.Index is { } index
                ? await engine.QueryIndexAsync(index, afterEntry, Queries.PageSize, cancellationToken).ConfigureAwait(false)
                : await engine.QueryPageAsync(afterId, Queries.PageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var document in page)
            {
                if (query.Where?.Invoke(document) ?? true)
                {
                    yield return document;
                }
            }

            if (page.Count < Queries.PageSize)
            {
                yield break;
            }

            afterId = page[^1].Id;
            afterEntry = query.Index is { } current ? Storage.LocalStoreIndexing.CursorOf(current, page[^1]) : null;
        }
    }

    /// <inheritdoc />
    public async Task<SyncWriteResult> SaveAsync(TDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await engine.WriteAsync(document, cancellationToken).ConfigureAwait(false);
        await _session.NotifyLocalWriteAsync(cancellationToken).ConfigureAwait(false);
        return new SyncWriteResult(receipt.Id, SyncConfirmation.SavedLocally);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncWriteResult>> SaveAllAsync(IReadOnlyList<TDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        var receipts = await engine.WriteGroupAsync(documents, cancellationToken).ConfigureAwait(false);
        await _session.NotifyLocalWriteAsync(cancellationToken).ConfigureAwait(false);
        return [.. receipts.Select(static r => new SyncWriteResult(r.Id, SyncConfirmation.SavedLocally))];
    }

    /// <inheritdoc />
    public async Task<SyncWriteResult> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await engine.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return new SyncWriteResult(id, SyncConfirmation.NotFound);
        }

        await _session.NotifyLocalWriteAsync(cancellationToken).ConfigureAwait(false);
        return new SyncWriteResult(id, SyncConfirmation.SavedLocally);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncDocumentConflict<TDocument>>> GetConflictsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        var records = await engine.GetConflictsAsync(limit, cancellationToken).ConfigureAwait(false);
        return [.. records.Select(r => new SyncDocumentConflict<TDocument>(r.Current.Id, r.Conflict!.Local, r.Conflict.Server, r.Conflict.Base))];
    }

    /// <inheritdoc />
    public async Task<SyncGoalResult> SyncAsync(SyncGoal goal, TimeSpan budget, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var account = await _resolveAccount(cancellationToken).ConfigureAwait(false);
        return await _session.SyncAsync(account, goal, budget, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SyncIssuePage> GetIssuesAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        var counts = await engine.CountIssuesAsync(cancellationToken).ConfigureAwait(false);
        var wanted = (int)Math.Min((long)offset + limit, int.MaxValue);
        var conflicts = offset < counts.Conflicts ? await engine.GetConflictsAsync(wanted, cancellationToken).ConfigureAwait(false) : [];
        var rejected = offset + limit > counts.Conflicts ? await engine.GetRejectedAsync(wanted, cancellationToken).ConfigureAwait(false) : [];
        var items = conflicts.Select(r => new SyncIssue(r.Current.Id, SyncIssueKind.Conflict))
            .Concat(rejected.Select(r => new SyncIssue(
                r.Current.Id,
                r.Rejection?.ErrorCode == Protocol.PushErrorCodes.GroupFailed ? SyncIssueKind.Blocked : SyncIssueKind.Rejected,
                r.Rejection?.ErrorCode,
                r.Rejection?.Message) { Arguments = r.Rejection?.Arguments }))
            .Skip(offset)
            .Take(limit)
            .ToList();
        return new SyncIssuePage(items, counts.Total);
    }

    /// <inheritdoc />
    public async Task<SyncWriteResult> ResolveConflictAsync(string id, TDocument resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        if (await engine.ResolveConflictAsync(id, resolved, cancellationToken).ConfigureAwait(false) is null)
        {
            return new SyncWriteResult(id, SyncConfirmation.NotFound);
        }

        await _session.NotifyLocalWriteAsync(cancellationToken).ConfigureAwait(false);
        return new SyncWriteResult(id, SyncConfirmation.SavedLocally);
    }

    /// <inheritdoc />
    public async Task<bool> DiscardConflictAsync(string id, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        if (!await engine.DiscardConflictAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _session.NotifyLocalWriteAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<SyncWriteResult> RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        if (await engine.RetryRejectedAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return new SyncWriteResult(id, SyncConfirmation.NotFound);
        }

        await _session.NotifyLocalWriteAsync(cancellationToken).ConfigureAwait(false);
        return new SyncWriteResult(id, SyncConfirmation.SavedLocally);
    }

    /// <inheritdoc />
    public async Task<bool> RevertAsync(string id, CancellationToken cancellationToken = default)
    {
        var engine = await EngineAsync(cancellationToken).ConfigureAwait(false);
        if (!await engine.RevertAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _session.NotifyLocalWriteAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _session.Changed += onChanged;
        return new Unsubscriber(() => _session.Changed -= onChanged);
    }

    private async Task<SyncEngine<TDocument>> EngineAsync(CancellationToken cancellationToken)
    {
        var account = await _resolveAccount(cancellationToken).ConfigureAwait(false);
        return await _session.GetEngineAsync(account, cancellationToken).ConfigureAwait(false);
    }
}
