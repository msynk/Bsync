using System.Diagnostics.CodeAnalysis;
using Bsync.Clocks;
using Bsync.Documents;

namespace Bsync.Storage;

/// <summary>
/// An in-memory <see cref="ILocalStore{TDocument}"/>. Useful for tests, prototypes and ephemeral
/// scenarios. It is <b>not durable</b>: everything is lost when the process ends. All stored states
/// are deep-cloned on the way in and out so callers can never mutate the store's internal copies.
/// </summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class InMemoryLocalStore<TDocument> : ILocalStore<TDocument>
    where TDocument : class, ISyncEntity
{
    private readonly Dictionary<string, SyncRecord<TDocument>> _records = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (SyncIndex<TDocument> Index, SortedSet<SyncIndexCursor> Entries, Dictionary<string, string> Keys)> _indexes = new(StringComparer.Ordinal);
    private readonly Func<TDocument, TDocument> _clone;
    private readonly object _gate = new();
    private ReplicaCursor _cursor = ReplicaCursor.Initial;
    private HlcTimestamp _highWater = HlcTimestamp.MinValue;

    /// <summary>Creates a store that clones with reflection-based JSON (not trim/AOT safe).</summary>
    [RequiresUnreferencedCode("Clones documents with reflection-based JSON. Use the constructor that takes a cloner for trimmed or AOT targets.")]
    [RequiresDynamicCode("Clones documents with reflection-based JSON. Use the constructor that takes a cloner for trimmed or AOT targets.")]
    public InMemoryLocalStore()
        : this(static doc => DocumentCloner.JsonClone(doc))
    {
    }

    /// <summary>Creates a store using the supplied deep-clone function.</summary>
    public InMemoryLocalStore(Func<TDocument, TDocument> cloner)
        : this(cloner, null)
    {
    }

    /// <summary>Creates a store using the supplied deep-clone function, maintaining <paramref name="indexes"/> (ADR-018).</summary>
    public InMemoryLocalStore(Func<TDocument, TDocument> cloner, IEnumerable<SyncIndex<TDocument>>? indexes)
    {
        ArgumentNullException.ThrowIfNull(cloner);
        _clone = cloner;
        foreach (var index in LocalStoreIndexing.Validate(indexes))
        {
            _indexes[index.Name] = (index, new SortedSet<SyncIndexCursor>(EntryOrder.Instance), new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    /// <inheritdoc />
    public Task<SyncRecord<TDocument>?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            return Task.FromResult(_records.TryGetValue(id, out var record) ? CloneRecord(record) : null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RecordUpdateResult<TDocument>>> UpdateAsync(
        IReadOnlyList<RecordUpdate<TDocument>> updates,
        ReplicaCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // Compute every new state first so that a throwing transform commits nothing.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var staged = new List<(string Id, SyncRecord<TDocument>? Record, bool Changed)>(updates.Count);
            foreach (var update in updates)
            {
                ArgumentNullException.ThrowIfNull(update);
                if (!seen.Add(update.Id))
                {
                    throw new ArgumentException($"Duplicate record id '{update.Id}' in one update.", nameof(updates));
                }

                var owned = new HashSet<TDocument>(ReferenceEqualityComparer.Instance);
                var existing = _records.TryGetValue(update.Id, out var stored) ? CloneRecord(stored, owned) : null;
                var next = update.Transform(existing);
                if (next is null)
                {
                    // Report the committed state, not the transform's working copy (it may have mutated it).
                    staged.Add((update.Id, stored, false));
                    continue;
                }

                if (!string.Equals(next.Current.Id, update.Id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Transform for '{update.Id}' returned a record with id '{next.Current.Id}'.");
                }

                // Documents the transform took from its input are copies this store made; only the others need copying.
                staged.Add((update.Id, CloneRecord(next, reuse: owned), true));
            }

            var results = new List<RecordUpdateResult<TDocument>>(staged.Count);
            foreach (var (id, record, changed) in staged)
            {
                if (changed)
                {
                    _records[id] = record!;
                    _ids.Add(id);
                    Reindex(id, record);
                    ObserveTimestamps(record!);
                }

                results.Add(new RecordUpdateResult<TDocument>(record is null ? null : CloneRecord(record), changed));
            }

            if (cursor is { } committed)
            {
                _cursor = committed;
            }

            return Task.FromResult<IReadOnlyList<RecordUpdateResult<TDocument>>>(results);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncRecord<TDocument>>> GetPendingAsync(
        int limit,
        IReadOnlySet<string>? exclude = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
        {
            var pending = _records.Values
                .Where(r => r.IsPushable && (exclude is null || !exclude.Contains(r.Current.Id)))
                .OrderBy(static r => r.Current.UpdatedAt)
                .ThenBy(static r => r.Current.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(r => CloneRecord(r))
                .ToList();

            return Task.FromResult<IReadOnlyList<SyncRecord<TDocument>>>(pending);
        }
    }

    /// <inheritdoc />
    public Task<int> CountDirtyAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_records.Values.Count(static r => r.IsDirty));
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeTombstonesAsync(long throughVersion, long generation, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var gone = _records
                .Where(kv => kv.Value is { IsDirty: false, Conflict: null, Current.Deleted: true, BaseVersion: { } version } record && version <= throughVersion && record.Generation == generation)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in gone)
            {
                Remove(id);
            }

            return Task.FromResult(gone.Count);
        }
    }

    /// <inheritdoc />
    public Task<SyncIssueCounts> CountIssuesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(new SyncIssueCounts(
                _records.Values.Count(static r => r.Conflict is not null),
                _records.Values.Count(static r => r.IsDirty && r.Rejection is not null),
                _records.Values.Count(static r => r.IsDirty && r.Rejection?.ErrorCode == Protocol.PushErrorCodes.GroupFailed)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TDocument>> QueryAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var documents = _records.Values
                .Where(r => !r.MissingAfterReset && (includeDeleted || !r.Current.Deleted))
                .Select(r => _clone(r.Current))
                .ToList();

            return Task.FromResult<IReadOnlyList<TDocument>>(documents);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TDocument>> QueryPageAsync(string? afterId, int limit, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
        {
            // The ids are kept sorted, so a page costs its own size, not the collection's.
            var ids = afterId is null ? _ids : _ids.GetViewBetween(afterId, _ids.Max ?? afterId);
            var documents = ids
                .Where(id => afterId is null || string.CompareOrdinal(id, afterId) > 0)
                .Select(id => _records[id])
                .Where(r => !r.MissingAfterReset && (includeDeleted || !r.Current.Deleted))
                .Take(limit)
                .Select(r => _clone(r.Current))
                .ToList();
            return Task.FromResult<IReadOnlyList<TDocument>>(documents);
        }
    }

    /// <inheritdoc />
    public Task<ReplicaCursor> GetCursorAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_cursor);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncRecord<TDocument>>> GetConflictsAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
        {
            var conflicts = _records.Values
                .Where(static r => r.Conflict is not null)
                .OrderBy(static r => r.Current.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(r => CloneRecord(r))
                .ToList();
            return Task.FromResult<IReadOnlyList<SyncRecord<TDocument>>>(conflicts);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncRecord<TDocument>>> GetRejectedAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
        {
            var rejected = _records.Values
                .Where(static r => r.Rejection is not null)
                .OrderBy(static r => r.Current.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(r => CloneRecord(r))
                .ToList();
            return Task.FromResult<IReadOnlyList<SyncRecord<TDocument>>>(rejected);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TDocument>> QueryIndexAsync(SyncIndexQuery<TDocument> query, SyncIndexCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
        {
            if (!_indexes.TryGetValue(query.Index.Name, out var index) || index.Index.Version != query.Index.Version)
            {
                return LocalStoreIndexing.QueryAsync(this, query, after, limit, cancellationToken);
            }

            var documents = Range(index.Entries, query, after)
                .Take(limit)
                .Select(e => _clone(_records[e.Id].Current))
                .ToList();
            return Task.FromResult<IReadOnlyList<TDocument>>(documents);
        }
    }

    /// <inheritdoc />
    public Task<int> CountIndexAsync(SyncIndexQuery<TDocument> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            return !_indexes.TryGetValue(query.Index.Name, out var index) || index.Index.Version != query.Index.Version
                ? LocalStoreIndexing.CountAsync(this, query, cancellationToken)
                : Task.FromResult(Range(index.Entries, query, null).Count());
        }
    }

    private static IEnumerable<SyncIndexCursor> Range(SortedSet<SyncIndexCursor> entries, SyncIndexQuery<TDocument> query, SyncIndexCursor? after)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        // Every entry with key K sorts between (K, "") and (K + U+FFFF, ""). The view narrows the set; Contains decides.
        var low = query.Lower is null ? entries.Min : new SyncIndexCursor(query.Lower, string.Empty);
        var high = query.Upper is null ? entries.Max : new SyncIndexCursor(query.UpperExclusive ? query.Upper : query.Upper + '\uffff', string.Empty);
        if (after is { } resume)
        {
            // Continue from the last entry returned instead of skipping everything before it.
            if (query.IsDescending && EntryOrder.Instance.Compare(resume, high) < 0)
            {
                high = resume;
            }
            else if (!query.IsDescending && EntryOrder.Instance.Compare(resume, low) > 0)
            {
                low = resume;
            }
        }

        if (EntryOrder.Instance.Compare(low, high) > 0)
        {
            return [];
        }

        // SortedSet<T>.Reverse streams the view backwards (LINQ's Reverse would copy it first).
        var set = entries.GetViewBetween(low, high);
        var view = (query.IsDescending ? set.Reverse() : set).Where(e => query.Contains(e.Key));
        return after is { } position ? view.Where(e => query.Compare(e, position) > 0) : view;
    }

    private void Reindex(string id, SyncRecord<TDocument>? record)
    {
        foreach (var (index, entries, keys) in _indexes.Values)
        {
            if (keys.Remove(id, out var old))
            {
                entries.Remove(new SyncIndexCursor(old, id));
            }

            if (record is { MissingAfterReset: false, Current.Deleted: false })
            {
                var key = index.KeyOf(record.Current);
                keys[id] = key;
                entries.Add(new SyncIndexCursor(key, id));
            }
        }
    }

    private bool Remove(string id)
    {
        if (!_records.Remove(id))
        {
            return false;
        }

        _ids.Remove(id);
        Reindex(id, null);
        return true;
    }

    /// <summary>Ordinal order of index entries: key, then id.</summary>
    private sealed class EntryOrder : IComparer<SyncIndexCursor>
    {
        public static EntryOrder Instance { get; } = new();

        public int Compare(SyncIndexCursor x, SyncIndexCursor y)
        {
            var c = string.CompareOrdinal(x.Key, y.Key);
            return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeAsync(IReadOnlyList<string> ids, long generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var removed = 0;
            foreach (var id in ids)
            {
                if (_records.TryGetValue(id, out var record) && !record.IsDirty && record.Conflict is null && record.Generation < generation && Remove(id))
                {
                    removed++;
                }
            }

            return Task.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncRecord<TDocument>>> GetStaleAsync(long generation, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
        {
            var stale = _records.Values
                .Where(r => !r.IsDirty && !r.MissingAfterReset && r.Generation < generation)
                .OrderBy(static r => r.Current.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(r => CloneRecord(r))
                .ToList();

            return Task.FromResult<IReadOnlyList<SyncRecord<TDocument>>>(stale);
        }
    }

    /// <inheritdoc />
    public Task<HlcTimestamp> GetClockHighWaterAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_highWater);
        }
    }

    /// <inheritdoc />
    public Task ResetClockHighWaterAsync(HlcTimestamp value, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _highWater = value;
            return Task.CompletedTask;
        }
    }

    private void ObserveTimestamps(SyncRecord<TDocument> record)
    {
        if (record.Current.UpdatedAt > _highWater)
        {
            _highWater = record.Current.UpdatedAt;
        }

        if (record.Pending is { } pending && pending.Payload.UpdatedAt > _highWater)
        {
            _highWater = pending.Payload.UpdatedAt;
        }
    }

    // One copy per distinct document instance: a record often holds the same document as current, base and pending
    // payload, and documents in records are never mutated in place, so the copy is shared within the copied record (D9).
    private SyncRecord<TDocument> CloneRecord(SyncRecord<TDocument> record, HashSet<TDocument>? owned = null, HashSet<TDocument>? reuse = null)
    {
        var copies = new Dictionary<TDocument, TDocument>(4, ReferenceEqualityComparer.Instance);
        TDocument Copy(TDocument document)
        {
            if (reuse is not null && reuse.Contains(document))
            {
                return document;
            }

            if (!copies.TryGetValue(document, out var copy))
            {
                copies[document] = copy = _clone(document);
                owned?.Add(copy);
            }

            return copy;
        }

        return record with
        {
            Current = Copy(record.Current),
            Base = record.Base is { } b ? Copy(b) : null,
            Observed = record.Observed is { } o ? Copy(o) : null,
            Conflict = record.Conflict is { } c ? c with { Server = Copy(c.Server), Local = Copy(c.Local), Base = c.Base is { } cb ? Copy(cb) : null } : null,
            Pending = record.Pending is { } p ? p with { Payload = Copy(p.Payload) } : null,
        };
    }
}
