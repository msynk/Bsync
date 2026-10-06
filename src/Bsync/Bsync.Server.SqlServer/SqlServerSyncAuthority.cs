using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bsync.Clocks;
using Bsync.Protocol;
using Microsoft.Data.SqlClient;

namespace Bsync.Server.SqlServer;

/// <summary>
/// A durable authority on the application's SQL Server database (ADR-005, ADR-009, ADR-014). Each (collection, scope)
/// has its own feed, versions and receipts in the <see cref="SqlServerSyncAuthorityOptions{TDocument}.Schema"/> schema,
/// so one instance serves every tenant; the caller's <see cref="SyncCallContext.Scope"/> selects the feed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Committed-prefix feed.</b> A push runs in one transaction that first reads its feed row with
/// <c>UPDLOCK, HOLDLOCK</c> and assigns versions from it. Another writer of the same feed waits for that lock, so a
/// version becomes visible only after every lower version of its feed committed or rolled back.
/// </para>
/// <para>
/// <b>Domain logic.</b> An <see cref="ISyncWriteHandler{TDocument}"/> runs in the same transaction for every write
/// about to be accepted, and may write the application's tables and return a canonical document. The write APIs
/// that take a <see cref="DbTransaction"/> enlist in the caller's transaction and never commit it, so an EF Core
/// <c>SaveChanges</c> and the feed can commit together.
/// </para>
/// <para>
/// <b>Restores.</b> After restoring the database, call <see cref="BeginNewEpochAsync"/> with a version floor at or
/// above anything the lost history may have issued (docs/operations/disaster-recovery.md).
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class SqlServerSyncAuthority<TDocument> : ISyncAuthority<TDocument>, ISyncDocumentReader<TDocument>, ISyncCommitNotifier, ISyncPublisher<TDocument>, ISyncRetentionTarget, IAsyncDisposable
    where TDocument : class, ISyncEntity
{
    private const string OperationSavepoint = "bs_op";
    private const string GroupSavepoint = "bs_group";

    private readonly SqlServerSyncAuthorityOptions<TDocument> _options;
    private readonly string _schema;
    private readonly IPhysicalClock _physical;
    private readonly byte[] _collectionKey;
    private readonly int _commandTimeout;
    private readonly HybridLogicalClock _publisherClock;
    private readonly object _pollGate = new();
    private Action<AuthorityCommit>? _committed;
    private CancellationTokenSource? _polling;
    private Task? _poller;
    private string? _epoch;

    private SqlServerSyncAuthority(SqlServerSyncAuthorityOptions<TDocument> options)
    {
        _options = options;
        _schema = SqlServerSchema.Quote(options.Schema);
        _physical = options.PhysicalClock ?? SystemPhysicalClock.Instance;
        _collectionKey = Key(options.Collection);
        _commandTimeout = (int)Math.Ceiling(options.CommandTimeout.TotalSeconds);
        _publisherClock = new HybridLogicalClock("server", _physical);
    }

    /// <summary>Creates the authority, creating or upgrading the schema if needed.</summary>
    /// <exception cref="SqlServerSchemaException">The database uses a newer schema.</exception>
    public static async Task<SqlServerSyncAuthority<TDocument>> CreateAsync(SqlServerSyncAuthorityOptions<TDocument> options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ConnectionString);
        ArgumentNullException.ThrowIfNull(options.DocumentType);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxOperationsPerPush, 1, nameof(options.MaxOperationsPerPush));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPageSize, 1, nameof(options.MaxPageSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxClockSkew, TimeSpan.Zero, nameof(options.MaxClockSkew));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CommandTimeout, TimeSpan.Zero, nameof(options.CommandTimeout));
        if ((options.Readers is null) != (options.PrincipalKey is null))
        {
            throw new ArgumentException("Readers and PrincipalKey are set together.", nameof(options));
        }
        if (!SyncIds.IsValid(options.Collection))
        {
            throw new ArgumentException("The collection name must be a valid identifier.", nameof(options));
        }

        var authority = new SqlServerSyncAuthority<TDocument>(options);
        authority._epoch = await SqlServerSchema.EnsureAsync(options.ConnectionString, options.Schema, cancellationToken).ConfigureAwait(false);
        return authority;
    }

    /// <inheritdoc />
    public AuthorityLimits Limits => new(_options.MaxOperationsPerPush, _options.MaxPageSize);

    /// <summary>Identifies the database's feed history (changes with <see cref="BeginNewEpochAsync"/>).</summary>
    public string Epoch => _epoch!;

    /// <inheritdoc />
    /// <remarks>
    /// Raised after this instance commits a push it owns, and, while there are subscribers, when polling the feed heads
    /// (<see cref="SqlServerSyncAuthorityOptions{TDocument}.CommitPollInterval"/>) finds commits by other processes (those
    /// carry no ids, and an own commit may be announced twice). After committing a caller's transaction, or when the host's
    /// own message bus reports a commit, call <see cref="NotifyCommitted"/>.
    /// </remarks>
    public event Action<AuthorityCommit>? Committed
    {
        add
        {
            lock (_pollGate)
            {
                _committed += value;
                if (_poller is null && _committed is not null && _options.CommitPollInterval > TimeSpan.Zero)
                {
                    _polling = new CancellationTokenSource();
                    _poller = Task.Run(() => PollAsync(_polling.Token));
                }
            }
        }

        remove
        {
            lock (_pollGate)
            {
                _committed -= value;
            }
        }
    }

    /// <summary>
    /// Announces committed changes to <see cref="Committed"/> subscribers: after committing a caller's transaction that
    /// wrote through this authority, or when another process reports a commit (host bridge). A hint only (I13).
    /// </summary>
    public void NotifyCommitted(AuthorityCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        _committed?.Invoke(commit);
    }

    /// <inheritdoc />
    public async Task<PullResult<TDocument>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Scope(context);
        if (request.BatchSize < 1)
        {
            throw new SyncProtocolException("The pull limit must be at least 1.");
        }

        try
        {
            return await RetryDeadlockAsync(() => PullCoreAsync(context, request, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException error) when (IsTransient(error))
        {
            throw Unavailable(error);
        }
    }

    private async Task<PullResult<TDocument>> PullCoreAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await RefreshEpochAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var since = ParseCheckpoint(context, request.Since);
        var limit = Math.Min(request.BatchSize, _options.MaxPageSize);
        if (_options.PrincipalKey is { } principalKey)
        {
            return await PullMembershipAsync(connection, context, request, principalKey(context), since, limit, cancellationToken).ConfigureAwait(false);
        }

        // The documents are read first and the retention horizon second: a purge between the two is then seen as a
        // raised horizon (reset), never as tombstones silently missing from the page.
        await using var command = Command(
            $"""
            SELECT TOP (@take) d.[id], d.[version], d.[document]
            FROM {_schema}.[documents] d JOIN {_schema}.[feeds] f ON f.[feed_id] = d.[feed_id]
            WHERE f.[collection_key] = @collection AND f.[scope_key] = @scope AND d.[version] > @since
            ORDER BY d.[version];
            SELECT [purged_through] FROM {_schema}.[feeds] WHERE [collection_key] = @collection AND [scope_key] = @scope;
            """,
            connection,
            null);
        command.Parameters.Add("@take", SqlDbType.Int).Value = limit + 1;
        AddFeedKey(command, context.Scope);
        command.Parameters.Add("@since", SqlDbType.BigInt).Value = since;

        var raw = new List<(string Id, long Version, string Json)>();
        long purgedThrough = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                raw.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }

            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                purgedThrough = reader.GetInt64(0);
            }
        }

        if (since > 0 && since < purgedThrough)
        {
            throw new SyncResetRequiredException("The checkpoint is older than the retention horizon.", ResetReasons.Expired);
        }

        var hasMore = raw.Count > limit;
        if (hasMore)
        {
            raw.RemoveAt(raw.Count - 1);
        }

        // Under locking READ COMMITTED a row updated during the scan can be read at its old and its new version; keep
        // the newest (ADR-014). Under READ_COMMITTED_SNAPSHOT the statement reads one snapshot and this does nothing.
        var newest = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            newest[raw[i].Id] = i;
        }

        var page = new List<RemoteChange<TDocument>>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            if (newest[raw[i].Id] != i)
            {
                continue;
            }

            var document = Deserialize(raw[i].Json);
            if (_options.CanRead?.Invoke(context, document) ?? true)
            {
                page.Add(new RemoteChange<TDocument>(document, raw[i].Version));
            }
        }

        var position = raw.Count > 0 ? raw[^1].Version : Math.Max(since, 0);
        return new PullResult<TDocument>(page, FormatCheckpoint(context, position), hasMore) { Features = [SyncFeatures.Groups, SyncFeatures.Limits, SyncFeatures.ServerTime, SyncFeatures.Retention], Limits = new SyncLimits(_options.MaxOperationsPerPush, _options.MaxPageSize), ServerTime = _physical.NowMilliseconds(), RetentionHorizon = purgedThrough };
    }

    /// <inheritdoc />
    public Task<PushResult<TDocument>> PushAsync(SyncCallContext context, PushRequest<TDocument> request, CancellationToken cancellationToken = default) =>
        PushAsync(context, request, transaction: null, cancellationToken);

    /// <summary>
    /// Decides each operation and returns one outcome per operation, in order. With <paramref name="transaction"/>, the
    /// writes enlist in the caller's transaction, which the caller commits (then calls <see cref="NotifyCommitted"/>) or
    /// rolls back; the authority never ends it.
    /// </summary>
    /// <exception cref="SyncTransportException">The request exceeds <see cref="Limits"/>, or the database is unavailable.</exception>
    public async Task<PushResult<TDocument>> PushAsync(SyncCallContext context, PushRequest<TDocument> request, DbTransaction? transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        Scope(context);
        if (request.Operations is null)
        {
            throw new SyncProtocolException("The push request has no operations.");
        }

        if (request.Operations.Count > _options.MaxOperationsPerPush)
        {
            throw new SyncTransportException(SyncErrorCodes.PayloadTooLarge, $"A push may carry at most {_options.MaxOperationsPerPush} operations.", isTransient: false);
        }

        var outcomes = await InTransactionAsync(
            transaction,
            (connection, active) => PushCoreAsync(connection, active, context, request.Operations, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var committed = request.Operations
            .Zip(outcomes)
            .Where(pair => pair.Second is { Kind: PushOutcomeKind.Accepted, IsDuplicate: false })
            .Select(pair => pair.First.DocumentId)
            .ToList();
        if (committed.Count > 0 && transaction is null)
        {
            _committed?.Invoke(new AuthorityCommit(context.Scope, committed));
        }

        return new PushResult<TDocument>(outcomes);
    }

    /// <inheritdoc />
    public Task<SyncPublishResult> UpsertAsync(string scope, TDocument document, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PublishAsync([scope], transaction, (write, ct) => PublishOneAsync(write, document, null, ct), [document.Id], cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncPublishResult> DeleteAsync(string scope, string id, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return PublishAsync([scope], transaction, (write, ct) => DeleteOneAsync(write, id, null, ct), [id], cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncPublishResult> ReplaceScopeAsync(string scope, IEnumerable<TDocument> documents, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var list = documents.ToList();
        return PublishAsync(
            [scope],
            transaction,
            async (write, ct) =>
            {
                var stored = await ReadFeedAsync(write, ct).ConfigureAwait(false);
                var keep = new HashSet<string>(StringComparer.Ordinal);
                var result = SyncPublishResult.None;
                foreach (var document in list)
                {
                    if (!keep.Add(document.Id))
                    {
                        throw new ArgumentException($"The document '{document.Id}' is listed twice.", nameof(documents));
                    }

                    result = result.Add(await PublishOneAsync(write, document, stored.GetValueOrDefault(document.Id), ct).ConfigureAwait(false));
                }

                foreach (var (id, current) in stored)
                {
                    if (!keep.Contains(id) && !current.Deleted)
                    {
                        result = result.Add(await DeleteOneAsync(write, id, current, ct).ConfigureAwait(false));
                    }
                }

                return result;
            },
            null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncPublishResult> PublishAsync(TDocument document, IEnumerable<string> scopes, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scopes);
        return PublishAsync([.. scopes], transaction, (write, ct) => PublishOneAsync(write, document, null, ct), [document.Id], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StoredDocument<TDocument>?> GetAsync(SyncCallContext context, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(id);
        Scope(context);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            $"""
            SELECT d.[version], d.[document] FROM {_schema}.[documents] d JOIN {_schema}.[feeds] f ON f.[feed_id] = d.[feed_id]
            WHERE f.[collection_key] = @collection AND f.[scope_key] = @scope AND d.[id_key] = @id
            """,
            connection,
            null);
        AddFeedKey(command, context.Scope);
        command.Parameters.Add("@id", SqlDbType.VarBinary, 512).Value = Key(id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var document = Deserialize(reader.GetString(1));
        return _options.CanRead?.Invoke(context, document) ?? true ? new StoredDocument<TDocument>(document, reader.GetInt64(0)) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredDocument<TDocument>>> ListAsync(SyncCallContext context, int limit, string? afterId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        Scope(context);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            $"""
            SELECT TOP (@take) d.[version], d.[document] FROM {_schema}.[documents] d JOIN {_schema}.[feeds] f ON f.[feed_id] = d.[feed_id]
            WHERE f.[collection_key] = @collection AND f.[scope_key] = @scope AND d.[deleted] = 0 AND (@after IS NULL OR d.[id_key] > @after)
            ORDER BY d.[id_key]
            """,
            connection,
            null);
        command.Parameters.Add("@take", SqlDbType.Int).Value = Math.Min(limit, _options.MaxPageSize);
        AddFeedKey(command, context.Scope);
        command.Parameters.Add("@after", SqlDbType.VarBinary, 512).Value = afterId is null ? DBNull.Value : Key(afterId);
        var page = new List<StoredDocument<TDocument>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var document = Deserialize(reader.GetString(1));
            if (_options.CanRead?.Invoke(context, document) ?? true)
            {
                page.Add(new StoredDocument<TDocument>(document, reader.GetInt64(0)));
            }
        }

        return page;
    }

    /// <summary>
    /// Removes tombstones of <paramref name="scope"/> at or below <paramref name="throughVersion"/> and raises its retention
    /// horizon (reset reason <c>expired</c> for older checkpoints; <c>base-expired</c> for edits based on purged documents).
    /// </summary>
    /// <returns>The number of tombstones removed.</returns>
    public Task<int> PurgeTombstonesAsync(string scope, long throughVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return InTransactionAsync(
            null,
            async (connection, transaction) =>
            {
                await using var command = Command(
                    $"""
                    DECLARE @feed int, @sequence bigint;
                    SELECT @feed = [feed_id], @sequence = [sequence] FROM {_schema}.[feeds] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE [collection_key] = @collection AND [scope_key] = @scope;
                    IF @feed IS NULL
                        SELECT 0;
                    ELSE
                    BEGIN
                        DECLARE @through bigint = CASE WHEN @requested < @sequence THEN @requested ELSE @sequence END;
                        DELETE FROM {_schema}.[documents] WHERE [feed_id] = @feed AND [deleted] = 1 AND [version] <= @through;
                        DECLARE @removed int = @@ROWCOUNT;
                        DELETE a FROM {_schema}.[document_access] a
                        WHERE a.[feed_id] = @feed AND NOT EXISTS (SELECT 1 FROM {_schema}.[documents] d WHERE d.[feed_id] = a.[feed_id] AND d.[id_key] = a.[id_key]);
                        UPDATE {_schema}.[feeds] SET [purged_through] = CASE WHEN [purged_through] > @through THEN [purged_through] ELSE @through END
                        WHERE [feed_id] = @feed;
                        SELECT @removed;
                    END
                    """,
                    connection,
                    transaction);
                AddFeedKey(command, scope);
                command.Parameters.Add("@requested", SqlDbType.BigInt).Value = throughVersion;
                return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            },
            cancellationToken);
    }

    /// <summary>Removes receipts of operations of <paramref name="scope"/> accepted at or below <paramref name="throughVersion"/>.</summary>
    /// <remarks>Keep receipts longer than any replica may stay offline; see docs/operations/disaster-recovery.md.</remarks>
    /// <returns>The number of receipts removed.</returns>
    public async Task<int> PurgeReceiptsAsync(string scope, long throughVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            $"""
            DELETE r FROM {_schema}.[receipts] r JOIN {_schema}.[feeds] f ON f.[feed_id] = r.[feed_id]
            WHERE f.[collection_key] = @collection AND f.[scope_key] = @scope AND r.[kind] = 0 AND r.[version] <= @through
            """,
            connection,
            null);
        AddFeedKey(command, scope);
        command.Parameters.Add("@through", SqlDbType.BigInt).Value = throughVersion;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a new epoch after the database was restored from a backup: every checkpoint issued before gets
    /// <c>reset-required</c> (<c>epoch</c>), and every feed, including feeds created later, continues above
    /// <paramref name="versionFloor"/>, which must be at or above any version the lost history may have issued. Affects
    /// every collection in the schema and every instance (they re-read the epoch on their next pull).
    /// </summary>
    public async Task BeginNewEpochAsync(long versionFloor, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(versionFloor);
        var epoch = SqlServerSchema.NewEpoch();
        await InTransactionAsync(
            null,
            async (connection, transaction) =>
            {
                await using var command = Command(
                    $"""
                    UPDATE {_schema}.[feeds] WITH (TABLOCKX) SET [sequence] = @floor WHERE [sequence] < @floor;
                    UPDATE {_schema}.[meta] SET [value] = @epoch WHERE [key] = 'epoch';
                    UPDATE {_schema}.[meta] SET [value] = CAST(@floor AS nvarchar(40))
                    WHERE [key] = 'version_floor' AND CAST([value] AS bigint) < @floor;
                    """,
                    connection,
                    transaction);
                command.Parameters.Add("@floor", SqlDbType.BigInt).Value = versionFloor;
                command.Parameters.Add("@epoch", SqlDbType.NVarChar, 400).Value = epoch;
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        _epoch = epoch;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, long>> GetFeedHeadsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command($"SELECT [scope], [sequence] FROM {_schema}.[feeds] WHERE [collection_key] = @collection", connection, null);
        command.Parameters.Add("@collection", SqlDbType.VarBinary, 512).Value = _collectionKey;
        var heads = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            heads[reader.GetString(0)] = reader.GetInt64(1);
        }

        return heads;
    }

    /// <summary>The highest version issued in <paramref name="scope"/> (diagnostics, restore planning).</summary>
    public async Task<long> GetHighestVersionAsync(string scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command($"SELECT [sequence] FROM {_schema}.[feeds] WHERE [collection_key] = @collection AND [scope_key] = @scope", connection, null);
        AddFeedKey(command, scope);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long sequence ? sequence : 0;
    }

    /// <summary>Stops polling for commits by other processes.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? poller;
        lock (_pollGate)
        {
            _polling?.Cancel();
            poller = _poller;
        }

        if (poller is not null)
        {
            try
            {
                await poller.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task PollAsync(CancellationToken stopping)
    {
        Dictionary<string, long>? known = null;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var heads = new Dictionary<string, long>(StringComparer.Ordinal);
                await using (var connection = await OpenAsync(stopping).ConfigureAwait(false))
                {
                    await using var command = Command($"SELECT [scope], [sequence] FROM {_schema}.[feeds] WHERE [collection_key] = @collection", connection, null);
                    command.Parameters.Add("@collection", SqlDbType.VarBinary, 512).Value = _collectionKey;
                    await using var reader = await command.ExecuteReaderAsync(stopping).ConfigureAwait(false);
                    while (await reader.ReadAsync(stopping).ConfigureAwait(false))
                    {
                        heads[reader.GetString(0)] = reader.GetInt64(1);
                    }
                }

                if (known is not null)
                {
                    foreach (var (scope, sequence) in heads)
                    {
                        if (!known.TryGetValue(scope, out var previous) || previous != sequence)
                        {
                            _committed?.Invoke(new AuthorityCommit(scope, []));
                        }
                    }
                }

                known = heads;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is SqlException or InvalidOperationException or IOException or TimeoutException)
            {
                // Hints are best effort (protocol §8): try again on the next tick; clients reconcile on their own schedule.
            }

            try
            {
                await Task.Delay(_options.CommitPollInterval, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<List<PushOutcome<TDocument>>> PushCoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        SyncCallContext context,
        IReadOnlyList<PushOperation<TDocument>> operations,
        CancellationToken cancellationToken)
    {
        var (feed, sequence, purgedThrough) = await LockFeedAsync(connection, transaction, context.Scope, cancellationToken).ConfigureAwait(false);
        var startSequence = sequence;
        var outcomes = new PushOutcome<TDocument>[operations.Count];
        var inRequest = new HashSet<string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var write = new WriteScope(connection, transaction, context, feed, purgedThrough, () => ++sequence);
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i]?.Group is { } group)
            {
                (groups.TryGetValue(group, out var members) ? members : groups[group] = []).Add(i);
                continue;
            }

            outcomes[i] = Visible(context, await ApplyAsync(write, operations[i], inRequest, cancellationToken).ConfigureAwait(false));
        }

        // Dependency groups (protocol §4.1): all members commit, or none. A savepoint undoes the members' writes, receipts
        // and handler work when one member is not accepted; the failed members' decisions are then recorded again.
        foreach (var (group, members) in groups)
        {
            if (!SyncIds.IsValid(group) || members.Any(i => operations[i].GroupSize != members.Count))
            {
                foreach (var i in members)
                {
                    inRequest.Add(operations[i]?.DocumentId ?? string.Empty);
                    outcomes[i] = PushOutcome<TDocument>.Rejected(operations[i]?.OperationId ?? string.Empty, PushErrorCodes.Invalid, "The dependency group is malformed or incomplete.");
                }

                continue;
            }

            transaction.Save(GroupSavepoint);
            var before = sequence;
            var decided = new List<PushOutcome<TDocument>>(members.Count);
            foreach (var i in members)
            {
                decided.Add(await ApplyAsync(write, operations[i], inRequest, cancellationToken).ConfigureAwait(false));
            }

            if (decided.All(o => o.Kind == PushOutcomeKind.Accepted))
            {
                for (var m = 0; m < members.Count; m++)
                {
                    outcomes[members[m]] = Visible(context, decided[m]);
                }

                continue;
            }

            transaction.Rollback(GroupSavepoint);
            sequence = before;
            for (var m = 0; m < members.Count; m++)
            {
                var operation = operations[members[m]];
                var outcome = decided[m];
                if (outcome.Kind == PushOutcomeKind.Accepted && !outcome.IsDuplicate)
                {
                    outcome = PushOutcome<TDocument>.RetryLater(operation.OperationId, PushErrorCodes.GroupAborted, "Another change of the same group was not accepted.");
                }
                else if (!outcome.IsDuplicate && outcome.Kind != PushOutcomeKind.RetryLater && outcome.ErrorCode is not (PushErrorCodes.Invalid or PushErrorCodes.OperationIdReused))
                {
                    await StoreReceiptAsync(write, operation, Serialize(operation.Document), outcome, cancellationToken).ConfigureAwait(false);
                }

                outcomes[members[m]] = Visible(context, outcome);
            }
        }

        if (sequence != startSequence)
        {
            await AdvanceAsync(connection, transaction, feed, sequence, cancellationToken).ConfigureAwait(false);
        }

        return [.. outcomes];
    }

    /// <summary>
    /// Runs a publisher write in each scope's feed, in one transaction (the caller's, if given). Feeds are locked in
    /// ordinal scope order, so concurrent fan-outs cannot deadlock on each other.
    /// </summary>
    private async Task<SyncPublishResult> PublishAsync(
        IReadOnlyList<string> scopes,
        DbTransaction? transaction,
        Func<WriteScope, CancellationToken, Task<SyncPublishResult>> publish,
        IReadOnlyList<string>? ids,
        CancellationToken cancellationToken)
    {
        var ordered = scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (ordered.Any(scope => !SyncIds.IsValid(scope)))
        {
            throw new ArgumentException("Every scope must be a valid identifier.", nameof(scopes));
        }

        var changed = new List<string>();
        var result = await InTransactionAsync(
            transaction,
            async (connection, active) =>
            {
                var total = SyncPublishResult.None;
                foreach (var scope in ordered)
                {
                    var (feed, sequence, purgedThrough) = await LockFeedAsync(connection, active, scope, cancellationToken).ConfigureAwait(false);
                    var start = sequence;
                    var write = new WriteScope(connection, active, SyncCallContext.Anonymous with { Scope = scope }, feed, purgedThrough, () => ++sequence);
                    total = total.Add(await publish(write, cancellationToken).ConfigureAwait(false));
                    if (sequence != start)
                    {
                        await AdvanceAsync(connection, active, feed, sequence, cancellationToken).ConfigureAwait(false);
                        changed.Add(scope);
                    }
                }

                return total;
            },
            cancellationToken).ConfigureAwait(false);

        if (transaction is null)
        {
            foreach (var scope in changed)
            {
                _committed?.Invoke(new AuthorityCommit(scope, ids ?? []));
            }
        }

        return result;
    }

    // Unchanged content (ignoring the timestamp) keeps its version, so rebuilding a projection adds no feed entries.
    private async Task<SyncPublishResult> PublishOneAsync(WriteScope write, TDocument document, StoredJson? known, CancellationToken cancellationToken)
    {
        SyncIds.Validate(document.Id);
        var current = known ?? await ReadOneAsync(write, document.Id, cancellationToken).ConfigureAwait(false);
        var stored = Deserialize(Serialize(document));
        if (current is not null)
        {
            stored.UpdatedAt = Deserialize(current.Json).UpdatedAt;
            if (Serialize(stored) == current.Json
                && (_options.Readers is not { } readers || (await ReadGrantedAsync(write, document.Id, cancellationToken).ConfigureAwait(false)).SetEquals(readers(stored))))
            {
                return new SyncPublishResult(0, 1, 0);
            }

            stored.UpdatedAt = document.UpdatedAt;
        }

        if (stored.UpdatedAt == default)
        {
            stored.UpdatedAt = _publisherClock.Now();
        }

        await WriteDocumentAsync(write, document.Id, current is not null, stored, cancellationToken).ConfigureAwait(false);
        return new SyncPublishResult(1, 0, 0);
    }

    private async Task<SyncPublishResult> DeleteOneAsync(WriteScope write, string id, StoredJson? known, CancellationToken cancellationToken)
    {
        var current = known ?? await ReadOneAsync(write, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return SyncPublishResult.None;
        }

        if (current.Deleted)
        {
            return new SyncPublishResult(0, 1, 0);
        }

        var tombstone = Deserialize(current.Json);
        tombstone.Deleted = true;
        tombstone.UpdatedAt = _publisherClock.Now();
        await WriteDocumentAsync(write, id, exists: true, tombstone, cancellationToken).ConfigureAwait(false);
        return new SyncPublishResult(0, 0, 1);
    }

    private async Task<StoredJson?> ReadOneAsync(WriteScope write, string id, CancellationToken cancellationToken)
    {
        await using var read = Command($"SELECT [deleted], [document] FROM {_schema}.[documents] WHERE [feed_id] = @feed AND [id_key] = @id", write.Connection, write.Transaction);
        read.Parameters.Add("@feed", SqlDbType.Int).Value = write.Feed;
        read.Parameters.Add("@id", SqlDbType.VarBinary, 512).Value = Key(id);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new StoredJson(reader.GetBoolean(0), reader.GetString(1)) : null;
    }

    private async Task<Dictionary<string, StoredJson>> ReadFeedAsync(WriteScope write, CancellationToken cancellationToken)
    {
        await using var read = Command($"SELECT [id], [deleted], [document] FROM {_schema}.[documents] WHERE [feed_id] = @feed", write.Connection, write.Transaction);
        read.Parameters.Add("@feed", SqlDbType.Int).Value = write.Feed;
        var stored = new Dictionary<string, StoredJson>(StringComparer.Ordinal);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stored[reader.GetString(0)] = new StoredJson(reader.GetBoolean(1), reader.GetString(2));
        }

        return stored;
    }

    private async Task<long> WriteDocumentAsync(WriteScope write, string id, bool exists, TDocument document, CancellationToken cancellationToken)
    {
        var version = write.NextVersion();
        await using var store = Command(
            exists
                ? $"UPDATE {_schema}.[documents] SET [version] = @version, [deleted] = @deleted, [document] = @document WHERE [feed_id] = @feed AND [id_key] = @id"
                : $"INSERT INTO {_schema}.[documents] ([feed_id], [id_key], [id], [version], [deleted], [document]) VALUES (@feed, @id, @name, @version, @deleted, @document)",
            write.Connection,
            write.Transaction);
        store.Parameters.Add("@feed", SqlDbType.Int).Value = write.Feed;
        store.Parameters.Add("@id", SqlDbType.VarBinary, 512).Value = Key(id);
        store.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = id;
        store.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
        store.Parameters.Add("@deleted", SqlDbType.Bit).Value = document.Deleted;
        store.Parameters.Add("@document", SqlDbType.NVarChar, -1).Value = Serialize(document);
        await store.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (_options.Readers is { } readers)
        {
            await WriteAccessAsync(write, id, document.Deleted ? null : readers(document), version, cancellationToken).ConfigureAwait(false);
        }

        return version;
    }

    /// <summary>
    /// ADR-015: moves every current reader's access row to <paramref name="version"/> (granted) and marks principals that
    /// just lost access as revoked at the same version. A tombstone (<paramref name="readers"/> null) goes to everyone who
    /// could read the document.
    /// </summary>
    private async Task WriteAccessAsync(WriteScope write, string id, IEnumerable<string>? readers, long version, CancellationToken cancellationToken)
    {
        var before = await ReadGrantedAsync(write, id, cancellationToken).ConfigureAwait(false);
        var after = readers is null ? before : readers.Where(SyncIds.IsValid).ToHashSet(StringComparer.Ordinal);
        var rows = after.Select(p => (Principal: p, Granted: true)).Concat(before.Where(p => !after.Contains(p)).Select(p => (Principal: p, Granted: false))).ToList();
        foreach (var chunk in rows.Chunk(500))
        {
            var sql = new StringBuilder();
            await using var command = Command(string.Empty, write.Connection, write.Transaction);
            command.Parameters.Add("@feed", SqlDbType.Int).Value = write.Feed;
            command.Parameters.Add("@id", SqlDbType.VarBinary, 512).Value = Key(id);
            command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = id;
            command.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
            for (var i = 0; i < chunk.Length; i++)
            {
                var granted = chunk[i].Granted ? 1 : 0;
                sql.Append(CultureInfo.InvariantCulture, $"""
                    UPDATE {_schema}.[document_access] SET [version] = @version, [granted] = {granted} WHERE [feed_id] = @feed AND [id_key] = @id AND [principal_key] = @p{i};
                    IF @@ROWCOUNT = 0 INSERT INTO {_schema}.[document_access] ([feed_id], [principal_key], [version], [id_key], [id], [granted]) VALUES (@feed, @p{i}, @version, @id, @name, {granted});

                    """);
                command.Parameters.Add($"@p{i}", SqlDbType.VarBinary, 512).Value = Key(chunk[i].Principal);
            }

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HashSet<string>> ReadGrantedAsync(WriteScope write, string id, CancellationToken cancellationToken)
    {
        await using var read = Command(
            $"SELECT [principal_key] FROM {_schema}.[document_access] WHERE [feed_id] = @feed AND [id_key] = @id AND [granted] = 1",
            write.Connection,
            write.Transaction);
        read.Parameters.Add("@feed", SqlDbType.Int).Value = write.Feed;
        read.Parameters.Add("@id", SqlDbType.VarBinary, 512).Value = Key(id);
        var granted = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            granted.Add(Encoding.BigEndianUnicode.GetString((byte[])reader[0]));
        }

        return granted;
    }

    /// <summary>
    /// ADR-015: reads the caller's access rows in version order (full pages; cost follows what the caller can see). Revoked
    /// rows become removals for replicas that asked for them; others must resnapshot (<c>scope-changed</c>).
    /// </summary>
    private async Task<PullResult<TDocument>> PullMembershipAsync(SqlConnection connection, SyncCallContext context, PullRequest request, string? key, long since, int limit, CancellationToken cancellationToken)
    {
        var raw = new List<(long Version, bool Granted, string Id, long? DocumentVersion, string? Json)>();
        long purgedThrough = 0;
        if (key is not null && SyncIds.IsValid(key))
        {
            await using var command = Command(
                $"""
                SELECT TOP (@take) a.[version], a.[granted], a.[id], d.[version], d.[document]
                FROM {_schema}.[document_access] a
                JOIN {_schema}.[feeds] f ON f.[feed_id] = a.[feed_id]
                LEFT JOIN {_schema}.[documents] d ON a.[granted] = 1 AND d.[feed_id] = a.[feed_id] AND d.[id_key] = a.[id_key]
                WHERE f.[collection_key] = @collection AND f.[scope_key] = @scope AND a.[principal_key] = @principal AND a.[version] > @since
                ORDER BY a.[version];
                SELECT [purged_through] FROM {_schema}.[feeds] WHERE [collection_key] = @collection AND [scope_key] = @scope;
                """,
                connection,
                null);
            command.Parameters.Add("@take", SqlDbType.Int).Value = limit + 1;
            AddFeedKey(command, context.Scope);
            command.Parameters.Add("@principal", SqlDbType.VarBinary, 512).Value = Key(key);
            command.Parameters.Add("@since", SqlDbType.BigInt).Value = since;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                raw.Add((reader.GetInt64(0), reader.GetBoolean(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                purgedThrough = reader.GetInt64(0);
            }
        }

        if (since > 0 && since < purgedThrough)
        {
            throw new SyncResetRequiredException("The checkpoint is older than the retention horizon.", ResetReasons.Expired);
        }

        var hasMore = raw.Count > limit;
        if (hasMore)
        {
            raw.RemoveAt(raw.Count - 1);
        }

        // As in the plain pull: a row updated during a locking scan can be read twice; keep its newest version.
        var newest = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            newest[raw[i].Id] = i;
        }

        var acceptsRemovals = request.Features?.Contains(SyncFeatures.Removals, StringComparer.Ordinal) == true;
        var changes = new List<RemoteChange<TDocument>>();
        var removals = new List<string>();
        for (var i = 0; i < raw.Count; i++)
        {
            var row = raw[i];
            if (newest[row.Id] != i)
            {
                continue;
            }

            if (row.Granted && row.Json is not null)
            {
                var document = Deserialize(row.Json);
                if (_options.CanRead?.Invoke(context, document) ?? true)
                {
                    changes.Add(new RemoteChange<TDocument>(document, row.DocumentVersion!.Value));
                }
            }
            else if (!row.Granted && since > 0)
            {
                // A snapshot from the start never needs a removal: the replica holds nothing it may not see.
                if (!acceptsRemovals)
                {
                    throw new SyncResetRequiredException("A document left this caller's view.", ResetReasons.ScopeChanged);
                }

                removals.Add(row.Id);
            }
        }

        var position = raw.Count > 0 ? raw[^1].Version : Math.Max(since, 0);
        return new PullResult<TDocument>(changes, FormatCheckpoint(context, position), hasMore)
        {
            Features = [SyncFeatures.Groups, SyncFeatures.Limits, SyncFeatures.ServerTime, SyncFeatures.Retention, SyncFeatures.Removals],
            Limits = new SyncLimits(_options.MaxOperationsPerPush, _options.MaxPageSize),
            ServerTime = _physical.NowMilliseconds(), RetentionHorizon = purgedThrough,
            Removals = acceptsRemovals ? removals : null,
        };
    }

    private async Task AdvanceAsync(SqlConnection connection, SqlTransaction transaction, int feed, long sequence, CancellationToken cancellationToken)
    {
        await using var advance = Command($"UPDATE {_schema}.[feeds] SET [sequence] = @sequence WHERE [feed_id] = @feed", connection, transaction);
        advance.Parameters.Add("@sequence", SqlDbType.BigInt).Value = sequence;
        advance.Parameters.Add("@feed", SqlDbType.Int).Value = feed;
        await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Locks the caller's feed row for the rest of the transaction, creating it (at the version floor) if needed.</summary>
    private async Task<(int Feed, long Sequence, long PurgedThrough)> LockFeedAsync(SqlConnection connection, SqlTransaction transaction, string scope, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            DECLARE @feed int, @sequence bigint, @purged bigint;
            SELECT @feed = [feed_id], @sequence = [sequence], @purged = [purged_through]
            FROM {_schema}.[feeds] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
            WHERE [collection_key] = @collection AND [scope_key] = @scope;
            IF @feed IS NULL
            BEGIN
                SELECT @sequence = CAST([value] AS bigint) FROM {_schema}.[meta] WHERE [key] = 'version_floor';
                INSERT INTO {_schema}.[feeds] ([collection_key], [scope_key], [collection], [scope], [sequence], [purged_through])
                VALUES (@collection, @scope, @collectionName, @scopeName, @sequence, 0);
                SELECT @feed = CAST(SCOPE_IDENTITY() AS int), @purged = 0;
            END
            SELECT @feed, @sequence, @purged;
            """,
            connection,
            transaction);
        AddFeedKey(command, scope);
        command.Parameters.Add("@collectionName", SqlDbType.NVarChar, 256).Value = _options.Collection;
        command.Parameters.Add("@scopeName", SqlDbType.NVarChar, 256).Value = scope;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private async Task<PushOutcome<TDocument>> ApplyAsync(WriteScope write, PushOperation<TDocument>? operation, HashSet<string> inRequest, CancellationToken cancellationToken)
    {
        if (operation is null || !SyncIds.IsValid(operation.OperationId))
        {
            return PushOutcome<TDocument>.Rejected(operation?.OperationId ?? string.Empty, PushErrorCodes.Invalid, "Missing or invalid operation id.");
        }

        var opId = operation.OperationId;
        if (!SyncIds.IsValid(operation.DocumentId)
            || operation.Document is null
            || !string.Equals(operation.Document.Id, operation.DocumentId, StringComparison.Ordinal)
            || operation.BaseVersion is < 1)
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.Invalid, "Malformed operation.");
        }

        if (!inRequest.Add(operation.DocumentId))
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.Invalid, "A push may contain one operation per document.");
        }

        var json = Serialize(operation.Document);
        var fingerprint = Fingerprint(operation, json);
        await using (var find = Command(
            $"SELECT [fingerprint], [kind], [version], [error_code], [message], [document] FROM {_schema}.[receipts] WHERE [feed_id] = @feed AND [operation_key] = @operation",
            write.Connection,
            write.Transaction))
        {
            find.Parameters.Add("@feed", SqlDbType.Int).Value = write.Feed;
            find.Parameters.Add("@operation", SqlDbType.VarBinary, 512).Value = Key(opId);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // A replay returns the stored outcome; nothing is decided again and the write handler is not called (I04, I11).
                if (reader.GetString(0) != fingerprint)
                {
                    return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.OperationIdReused, "The operation id was already used for a different request.");
                }

                return new PushOutcome<TDocument>(opId, (PushOutcomeKind)reader.GetInt16(1))
                {
                    Version = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    ErrorCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Message = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Document = reader.IsDBNull(5) ? null : Deserialize(reader.GetString(5)),
                    IsDuplicate = true,
                };
            }
        }

        var outcome = await DecideAsync(write, operation, json, cancellationToken).ConfigureAwait(false);
        if (outcome.Kind != PushOutcomeKind.RetryLater)
        {
            await StoreReceiptAsync(write, operation, json, outcome, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<PushOutcome<TDocument>> DecideAsync(WriteScope write, PushOperation<TDocument> operation, string json, CancellationToken cancellationToken)
    {
        var opId = operation.OperationId;
        if (operation.Document.UpdatedAt.WallTime > _physical.NowMilliseconds() + (long)_options.MaxClockSkew.TotalMilliseconds)
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.ClockSkew, "The document's timestamp is too far in the future.");
        }

        var idKey = Key(operation.DocumentId);
        StoredDocument<TDocument>? current = null;
        await using (var read = Command($"SELECT [version], [document] FROM {_schema}.[documents] WHERE [feed_id] = @feed AND [id_key] = @id", write.Connection, write.Transaction))
        {
            read.Parameters.Add("@feed", SqlDbType.Int).Value = write.Feed;
            read.Parameters.Add("@id", SqlDbType.VarBinary, 512).Value = idKey;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                current = new StoredDocument<TDocument>(Deserialize(reader.GetString(1)), reader.GetInt64(0));
            }
        }

        if (current is null && operation.BaseVersion is { } baseVersion && baseVersion <= write.PurgedThrough)
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.BaseExpired, "The document no longer exists on the server.");
        }

        // ADR-015: only a current reader may change an existing document.
        if (current is not null && _options.PrincipalKey is { } principalKey
            && (principalKey(write.Context) is not { } key || !(await ReadGrantedAsync(write, operation.DocumentId, cancellationToken).ConfigureAwait(false)).Contains(key)))
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.Forbidden);
        }

        if (_options.CanWrite is { } canWrite && !canWrite(write.Context, operation, current?.Document))
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.Forbidden);
        }

        if (_options.Validator?.Invoke(write.Context, operation, current?.Document) is { } error)
        {
            return PushOutcome<TDocument>.Rejected(opId, error);
        }

        if (current is not null && operation.BaseVersion != current.Version)
        {
            return PushOutcome<TDocument>.Conflict(opId, current.Version, current.Document);
        }

        var canonical = json;
        if (_options.WriteHandler is { } handler)
        {
            write.Transaction.Save(OperationSavepoint);
            var offered = operation with { Document = Deserialize(json) };
            var stored = current is null ? null : new StoredDocument<TDocument>(Deserialize(Serialize(current.Document)), current.Version);
            var decision = await handler.HandleAsync(new SyncWriteContext<TDocument>(write.Context, offered, stored, write.Connection, write.Transaction), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The write handler returned no decision.");
            switch (decision.Kind)
            {
                case SyncWriteDecisionKind.Accept:
                    if (!string.Equals(decision.Document!.Id, operation.DocumentId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The write handler accepted a document with a different id.");
                    }

                    canonical = Serialize(decision.Document);
                    break;

                case SyncWriteDecisionKind.Conflict:
                    write.Transaction.Rollback(OperationSavepoint);
                    return current is null
                        ? throw new InvalidOperationException("The write handler answered a conflict for a document that does not exist.")
                        : PushOutcome<TDocument>.Conflict(opId, current.Version, current.Document);

                case SyncWriteDecisionKind.Reject:
                    write.Transaction.Rollback(OperationSavepoint);
                    return PushOutcome<TDocument>.Rejected(opId, decision.ErrorCode!, decision.Message);

                default:
                    write.Transaction.Rollback(OperationSavepoint);
                    return PushOutcome<TDocument>.RetryLater(opId, decision.ErrorCode!, decision.Message);
            }
        }

        var accepted = Deserialize(canonical);
        var version = await WriteDocumentAsync(write, operation.DocumentId, current is not null, accepted, cancellationToken).ConfigureAwait(false);
        return PushOutcome<TDocument>.Accepted(opId, version, accepted);
    }

    private async Task StoreReceiptAsync(WriteScope write, PushOperation<TDocument> operation, string json, PushOutcome<TDocument> outcome, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            INSERT INTO {_schema}.[receipts] ([feed_id], [operation_key], [fingerprint], [kind], [version], [error_code], [message], [document])
            VALUES (@feed, @operation, @fingerprint, @kind, @version, @error, @message, @document)
            """,
            write.Connection,
            write.Transaction);
        command.Parameters.Add("@feed", SqlDbType.Int).Value = write.Feed;
        command.Parameters.Add("@operation", SqlDbType.VarBinary, 512).Value = Key(operation.OperationId);
        command.Parameters.Add("@fingerprint", SqlDbType.Char, 64).Value = Fingerprint(operation, json);
        command.Parameters.Add("@kind", SqlDbType.SmallInt).Value = (short)outcome.Kind;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = (object?)outcome.Version ?? DBNull.Value;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 256).Value = (object?)outcome.ErrorCode ?? DBNull.Value;
        command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = (object?)outcome.Message ?? DBNull.Value;
        command.Parameters.Add("@document", SqlDbType.NVarChar, -1).Value = outcome.Document is { } document ? Serialize(document) : DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> InTransactionAsync<T>(DbTransaction? caller, Func<SqlConnection, SqlTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        if (caller is not null)
        {
            var enlisted = caller as SqlTransaction
                ?? throw new ArgumentException("The transaction must be a Microsoft.Data.SqlClient transaction.", nameof(caller));
            var connection = enlisted.Connection
                ?? throw new ArgumentException("The transaction has already completed.", nameof(caller));
            if (enlisted.IsolationLevel == IsolationLevel.Snapshot)
            {
                // Under SNAPSHOT a read sees the transaction's start, not the latest commit under the feed lock.
                throw new ArgumentException("SNAPSHOT transactions are not supported; use READ COMMITTED (the default).", nameof(caller));
            }

            return await work(connection, enlisted).ConfigureAwait(false);
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            var result = await work(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (SqlException error) when (IsTransient(error))
        {
            // Nothing was committed; the client resends the same operations.
            throw Unavailable(error);
        }
    }

    /// <summary>Retries a read chosen as a deadlock victim (safe: it changed nothing) a few times before reporting it.</summary>
    private static async Task<T> RetryDeadlockAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (SqlException error) when (attempt < 4 && error.Number == 1205)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SqlCommand Command(string sql, SqlConnection connection, SqlTransaction? transaction) =>
        new(sql, connection, transaction) { CommandTimeout = _commandTimeout };

    private void AddFeedKey(SqlCommand command, string scope)
    {
        command.Parameters.Add("@collection", SqlDbType.VarBinary, 512).Value = _collectionKey;
        command.Parameters.Add("@scope", SqlDbType.VarBinary, 512).Value = Key(scope);
    }

    private async Task RefreshEpochAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        // Another instance may have started a new epoch; read it once per pull (a primary-key lookup).
        await using var command = Command($"SELECT [value] FROM {_schema}.[meta] WHERE [key] = 'epoch'", connection, transaction);
        _epoch = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw new InvalidOperationException("The Bsync schema has no epoch.");
    }

    private static void Scope(SyncCallContext context)
    {
        if (!SyncIds.IsValid(context.Scope))
        {
            throw new SyncTransportException(SyncErrorCodes.Forbidden, "No valid scope for the caller.", isTransient: false);
        }
    }

    private PushOutcome<TDocument> Visible(SyncCallContext context, PushOutcome<TDocument> outcome) =>
        outcome.Document is { } document
        && ((_options.CanRead is { } canRead && !canRead(context, document))
            || (_options.PrincipalKey is { } principalKey && (principalKey(context) is not { } key || !_options.Readers!(document).Contains(key, StringComparer.Ordinal))))
            ? PushOutcome<TDocument>.Rejected(outcome.OperationId, PushErrorCodes.Forbidden) with { IsDuplicate = outcome.IsDuplicate }
            : outcome;

    private string Serialize(TDocument document) => JsonSerializer.Serialize(document, _options.DocumentType);

    private TDocument Deserialize(string json) =>
        JsonSerializer.Deserialize(json, _options.DocumentType) ?? throw new InvalidOperationException("A stored document deserialized to null.");

    private static byte[] Key(string value) => Encoding.BigEndianUnicode.GetBytes(value);

    private static string Fingerprint(PushOperation<TDocument> operation, string json)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture, $"{operation.DocumentId.Length}:{operation.DocumentId}|{operation.BaseVersion}|{operation.Group}|{operation.GroupSize}|{json}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private Checkpoint FormatCheckpoint(SyncCallContext context, long position) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{Epoch}~{ScopeHash(context)}:{position}"));

    // A checkpoint names a position in one feed: bind the collection, the scope and what the caller may see. Each part is
    // length-prefixed, so different (collection, scope, fingerprint) triples never encode to the same text.
    private string ScopeHash(SyncCallContext context)
    {
        var fingerprint = _options.ScopeFingerprint?.Invoke(context) ?? string.Empty;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{_options.Collection.Length}:{_options.Collection}|{context.Scope.Length}:{context.Scope}|{fingerprint.Length}:{fingerprint}");

        // With membership a checkpoint names a position in one principal's view.
        if (_options.PrincipalKey is { } principalKey)
        {
            var key = principalKey(context) ?? string.Empty;
            text += string.Create(CultureInfo.InvariantCulture, $"|{key.Length}:{key}");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private long ParseCheckpoint(SyncCallContext context, Checkpoint checkpoint)
    {
        if (checkpoint.IsStart)
        {
            return 0;
        }

        var value = checkpoint.Value!;
        var separator = value.LastIndexOf(':');
        var scopeSeparator = value.LastIndexOf('~', Math.Max(separator, 0));
        if (separator <= 0 || !long.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var position))
        {
            throw new SyncProtocolException($"Malformed checkpoint '{value}'.");
        }

        if (scopeSeparator <= 0 || !value.AsSpan(0, scopeSeparator).SequenceEqual(Epoch))
        {
            throw new SyncResetRequiredException("The checkpoint was issued by a different server epoch.", ResetReasons.Epoch);
        }

        if (!value.AsSpan(scopeSeparator + 1, separator - scopeSeparator - 1).SequenceEqual(ScopeHash(context)))
        {
            throw new SyncResetRequiredException("What this caller may see has changed since the checkpoint was issued.", ResetReasons.ScopeChanged);
        }

        return position;
    }

    // Deadlock victim, lock or command timeout, connection loss, and Azure SQL throttling or failover.
    private static bool IsTransient(SqlException error) =>
        error.Errors.Cast<SqlError>().Any(e => e.Number is 1205 or 1222 or -2 or 53 or 64 or 233 or 10053 or 10054 or 10060
            or 4060 or 40143 or 40197 or 40501 or 40613 or 49918 or 49919 or 49920 or 10928 or 10929);

    private static SyncTransportException Unavailable(SqlException error) =>
        new(SyncErrorCodes.Unavailable, "The database is temporarily unavailable.", isTransient: true, retryAfter: TimeSpan.FromSeconds(1), innerException: error);

    private sealed record StoredJson(bool Deleted, string Json);

    private sealed record WriteScope(SqlConnection Connection, SqlTransaction Transaction, SyncCallContext Context, int Feed, long PurgedThrough, Func<long> NextVersion);
}
