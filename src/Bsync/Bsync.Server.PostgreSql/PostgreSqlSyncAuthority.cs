using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bsync.Clocks;
using Bsync.Protocol;
using Npgsql;
using NpgsqlTypes;

namespace Bsync.Server.PostgreSql;

/// <summary>
/// A durable authority on PostgreSQL (ADR-005, ADR-009). Each (collection, scope) has its own feed, versions and
/// receipts, so one instance serves every tenant; the caller's <see cref="SyncCallContext.Scope"/> selects the feed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Committed-prefix feed.</b> A push runs in one transaction that first locks its feed row
/// (<c>SELECT … FOR UPDATE</c>) and assigns versions from it. Another writer of the same feed waits for the lock, so
/// versions become visible in the order they are assigned: a pull can never see version <c>n + 1</c> while <c>n</c> is
/// uncommitted, and no checkpoint can skip a change that commits later. Writers of different scopes do not wait for
/// each other. Any number of server processes may share the database.
/// </para>
/// <para>
/// <b>Atomicity.</b> Each push request commits all of its outcomes and receipts together, or none (a failed
/// transaction is reported to the client as <c>unavailable</c> and retried with the same operation ids).
/// </para>
/// <para>
/// <b>Restores.</b> After restoring the database from a backup, call <see cref="BeginNewEpochAsync"/> with a version
/// floor at or above anything the lost history may have issued; replicas then reset (docs/operations/disaster-recovery.md).
/// </para>
/// <para>
/// <b>Hints.</b> Commits are announced with <c>NOTIFY</c>; every instance that has subscribers to
/// <see cref="Committed"/> listens, so a commit on one server process reaches clients connected to another.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class PostgreSqlSyncAuthority<TDocument> : ISyncAuthority<TDocument>, ISyncDocumentReader<TDocument>, ISyncCommitNotifier, ISyncPublisher<TDocument>, ISyncRetentionTarget, IAsyncDisposable
    where TDocument : class, ISyncEntity
{
    /// <summary>The <c>NOTIFY</c> channel used for commit hints.</summary>
    public const string Channel = "bsync_commits";

    private readonly PostgreSqlSyncAuthorityOptions<TDocument> _options;
    private readonly NpgsqlDataSource _source;
    private readonly IPhysicalClock _physical;
    private readonly HybridLogicalClock _publisherClock;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly object _listenGate = new();
    private Action<AuthorityCommit>? _committed;
    private CancellationTokenSource? _listening;
    private Task? _listener;
    private string? _epoch;

    private PostgreSqlSyncAuthority(PostgreSqlSyncAuthorityOptions<TDocument> options)
    {
        _options = options;
        _source = options.DataSource;
        _physical = options.PhysicalClock ?? SystemPhysicalClock.Instance;
        _publisherClock = new HybridLogicalClock("server", _physical);
    }

    /// <summary>Creates the authority, creating or upgrading the schema if needed.</summary>
    /// <exception cref="PostgreSqlSchemaException">The database uses a newer schema.</exception>
    public static async Task<PostgreSqlSyncAuthority<TDocument>> CreateAsync(PostgreSqlSyncAuthorityOptions<TDocument> options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.DataSource);
        ArgumentNullException.ThrowIfNull(options.DocumentType);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxOperationsPerPush, 1, nameof(options.MaxOperationsPerPush));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPageSize, 1, nameof(options.MaxPageSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxClockSkew, TimeSpan.Zero, nameof(options.MaxClockSkew));
        if (!SyncIds.IsValid(options.Collection))
        {
            throw new ArgumentException("The collection name must be a valid identifier.", nameof(options));
        }

        if ((options.Readers is null) != (options.PrincipalKey is null))
        {
            throw new ArgumentException("Readers and PrincipalKey are set together.", nameof(options));
        }

        var authority = new PostgreSqlSyncAuthority<TDocument>(options);
        authority._epoch = await PostgreSqlSchema.EnsureAsync(options.DataSource, cancellationToken).ConfigureAwait(false);
        return authority;
    }

    /// <inheritdoc />
    public AuthorityLimits Limits => new(_options.MaxOperationsPerPush, _options.MaxPageSize);

    /// <summary>Identifies the database's feed history (changes with <see cref="BeginNewEpochAsync"/>).</summary>
    public string Epoch => _epoch!;

    /// <inheritdoc />
    public async Task<PullResult<TDocument>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Scope(context);
        if (request.BatchSize < 1)
        {
            throw new SyncProtocolException("The pull limit must be at least 1.");
        }

        await RefreshEpochAsync(cancellationToken).ConfigureAwait(false);
        var since = ParseCheckpoint(context, request.Since);
        var limit = Math.Min(request.BatchSize, _options.MaxPageSize);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var purgedThrough = await ScalarAsync<long?>(connection, transaction, "SELECT purged_through FROM bs_feeds WHERE collection = $1 AND scope = $2", cancellationToken, _options.Collection, context.Scope).ConfigureAwait(false) ?? 0;
        if (since > 0 && since < purgedThrough)
        {
            throw new SyncResetRequiredException("The checkpoint is older than the retention horizon.", ResetReasons.Expired);
        }

        if (_options.PrincipalKey is { } principalKey)
        {
            var membership = await PullMembershipAsync(connection, transaction, context, request, principalKey(context), since, limit, purgedThrough, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return membership;
        }

        await using var command = new NpgsqlCommand(
            "SELECT id, version, document FROM bs_documents WHERE collection = $1 AND scope = $2 AND version > $3 ORDER BY version LIMIT $4",
            connection,
            transaction);
        command.Parameters.Add(new() { Value = _options.Collection });
        command.Parameters.Add(new() { Value = context.Scope });
        command.Parameters.Add(new() { Value = since });
        command.Parameters.Add(new() { Value = limit + 1 });
        var window = new List<(long Version, TDocument Document)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                window.Add((reader.GetInt64(1), Deserialize(reader.GetString(2))));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = window.Count > limit;
        if (hasMore)
        {
            window.RemoveAt(window.Count - 1);
        }

        var page = window
            .Where(e => _options.CanRead?.Invoke(context, e.Document) ?? true)
            .Select(e => new RemoteChange<TDocument>(e.Document, e.Version))
            .ToList();
        var position = window.Count > 0 ? window[^1].Version : Math.Max(since, 0);
        return new PullResult<TDocument>(page, FormatCheckpoint(context, position), hasMore) { Features = [SyncFeatures.Groups, SyncFeatures.Limits, SyncFeatures.ServerTime, SyncFeatures.Retention], Limits = new SyncLimits(_options.MaxOperationsPerPush, _options.MaxPageSize), ServerTime = _physical.NowMilliseconds(), RetentionHorizon = purgedThrough };
    }

    /// <inheritdoc />
    public async Task<PushResult<TDocument>> PushAsync(SyncCallContext context, PushRequest<TDocument> request, CancellationToken cancellationToken = default)
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

        List<PushOutcome<TDocument>> outcomes;
        try
        {
            outcomes = await PushCoreAsync(context, request.Operations, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException error) when (error.IsTransient || error is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
        {
            // Nothing was committed; the client resends the same operations.
            throw new SyncTransportException(SyncErrorCodes.Unavailable, "The database is temporarily unavailable.", isTransient: true, retryAfter: TimeSpan.FromSeconds(1));
        }

        var committed = request.Operations
            .Zip(outcomes)
            .Where(pair => pair.Second is { Kind: PushOutcomeKind.Accepted, IsDuplicate: false })
            .Select(pair => pair.First.DocumentId)
            .ToList();
        if (committed.Count > 0)
        {
            _committed?.Invoke(new AuthorityCommit(context.Scope, committed));
        }

        return new PushResult<TDocument>(outcomes);
    }

    private async Task<List<PushOutcome<TDocument>>> PushCoreAsync(SyncCallContext context, IReadOnlyList<PushOperation<TDocument>> operations, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Lock the feed: versions of this scope are assigned and committed one transaction at a time.
        await ExecuteAsync(connection, transaction, "INSERT INTO bs_feeds (collection, scope, sequence, purged_through) VALUES ($1, $2, 0, 0) ON CONFLICT DO NOTHING", cancellationToken, _options.Collection, context.Scope).ConfigureAwait(false);
        long sequence, purgedThrough;
        await using (var lockFeed = new NpgsqlCommand("SELECT sequence, purged_through FROM bs_feeds WHERE collection = $1 AND scope = $2 FOR UPDATE", connection, transaction))
        {
            lockFeed.Parameters.Add(new() { Value = _options.Collection });
            lockFeed.Parameters.Add(new() { Value = context.Scope });
            await using var reader = await lockFeed.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            (sequence, purgedThrough) = (reader.GetInt64(0), reader.GetInt64(1));
        }

        var startSequence = sequence;
        var outcomes = new PushOutcome<TDocument>[operations.Count];
        var inRequest = new HashSet<string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i]?.Group is { } group)
            {
                (groups.TryGetValue(group, out var members) ? members : groups[group] = []).Add(i);
                continue;
            }

            outcomes[i] = Visible(context, await ApplyAsync(connection, transaction, context, operations[i], inRequest, purgedThrough, () => ++sequence, cancellationToken).ConfigureAwait(false));
        }

        // Dependency groups (protocol §4.1): all members commit, or none. A savepoint undoes the members' writes and
        // receipts when one of them is not accepted; the failed members' decisions are then recorded again.
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

            await transaction.SaveAsync("bs_group", cancellationToken).ConfigureAwait(false);
            var before = sequence;
            var decided = new List<PushOutcome<TDocument>>(members.Count);
            foreach (var i in members)
            {
                decided.Add(await ApplyAsync(connection, transaction, context, operations[i], inRequest, purgedThrough, () => ++sequence, cancellationToken).ConfigureAwait(false));
            }

            if (decided.All(o => o.Kind == PushOutcomeKind.Accepted))
            {
                await transaction.ReleaseAsync("bs_group", cancellationToken).ConfigureAwait(false);
                for (var m = 0; m < members.Count; m++)
                {
                    outcomes[members[m]] = Visible(context, decided[m]);
                }

                continue;
            }

            await transaction.RollbackAsync("bs_group", cancellationToken).ConfigureAwait(false);
            sequence = before;
            for (var m = 0; m < members.Count; m++)
            {
                var operation = operations[members[m]];
                var outcome = decided[m];
                if (outcome.Kind == PushOutcomeKind.Accepted && !outcome.IsDuplicate)
                {
                    outcome = PushOutcome<TDocument>.RetryLater(operation.OperationId, PushErrorCodes.GroupAborted, "Another change of the same group was not accepted.");
                }
                else if (!outcome.IsDuplicate && outcome.ErrorCode is not (PushErrorCodes.Invalid or PushErrorCodes.OperationIdReused))
                {
                    await StoreReceiptAsync(connection, transaction, context, operation, outcome, cancellationToken).ConfigureAwait(false);
                }

                outcomes[members[m]] = Visible(context, outcome);
            }
        }

        if (sequence != startSequence)
        {
            await ExecuteAsync(connection, transaction, "UPDATE bs_feeds SET sequence = $3 WHERE collection = $1 AND scope = $2", cancellationToken, _options.Collection, context.Scope, sequence).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "SELECT pg_notify($1, $2)", cancellationToken, Channel, $"{_instance}\u001f{_options.Collection}\u001f{context.Scope}").ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. outcomes];
    }

    private async Task<PushOutcome<TDocument>> ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SyncCallContext context,
        PushOperation<TDocument>? operation,
        HashSet<string> inRequest,
        long purgedThrough,
        Func<long> nextVersion,
        CancellationToken cancellationToken)
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
        await using (var find = new NpgsqlCommand(
            "SELECT fingerprint, kind, version, error_code, message, document FROM bs_receipts WHERE collection = $1 AND scope = $2 AND operation_id = $3",
            connection,
            transaction))
        {
            find.Parameters.Add(new() { Value = _options.Collection });
            find.Parameters.Add(new() { Value = context.Scope });
            find.Parameters.Add(new() { Value = opId });
            await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
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

        var outcome = await DecideAsync(connection, transaction, context, operation, json, purgedThrough, nextVersion, cancellationToken).ConfigureAwait(false);
        await StoreReceiptAsync(connection, transaction, context, operation, outcome, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private Task StoreReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SyncCallContext context, PushOperation<TDocument> operation, PushOutcome<TDocument> outcome, CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO bs_receipts (collection, scope, operation_id, fingerprint, kind, version, error_code, message, document) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)",
            cancellationToken,
            _options.Collection,
            context.Scope,
            operation.OperationId,
            Fingerprint(operation, Serialize(operation.Document)),
            (short)outcome.Kind,
            (object?)outcome.Version ?? DBNull.Value,
            (object?)outcome.ErrorCode ?? DBNull.Value,
            (object?)outcome.Message ?? DBNull.Value,
            outcome.Document is { } document ? Serialize(document) : DBNull.Value);

    private async Task<PushOutcome<TDocument>> DecideAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SyncCallContext context,
        PushOperation<TDocument> operation,
        string json,
        long purgedThrough,
        Func<long> nextVersion,
        CancellationToken cancellationToken)
    {
        var opId = operation.OperationId;
        if (operation.Document.UpdatedAt.WallTime > _physical.NowMilliseconds() + (long)_options.MaxClockSkew.TotalMilliseconds)
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.ClockSkew, "The document's timestamp is too far in the future.");
        }

        StoredDocument<TDocument>? current = null;
        await using (var read = new NpgsqlCommand("SELECT version, document FROM bs_documents WHERE collection = $1 AND scope = $2 AND id = $3", connection, transaction))
        {
            read.Parameters.Add(new() { Value = _options.Collection });
            read.Parameters.Add(new() { Value = context.Scope });
            read.Parameters.Add(new() { Value = operation.DocumentId });
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                current = new StoredDocument<TDocument>(Deserialize(reader.GetString(1)), reader.GetInt64(0));
            }
        }

        if (current is null && operation.BaseVersion is { } baseVersion && baseVersion <= purgedThrough)
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.BaseExpired, "The document no longer exists on the server.");
        }

        if (_options.CanWrite is { } canWrite && !canWrite(context, operation, current?.Document))
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.Forbidden);
        }

        // ADR-015: changing an existing document requires read access to it.
        if (current is not null && _options.PrincipalKey is { } principalKey
            && (principalKey(context) is not { } key || !(await ReadGrantedAsync(connection, transaction, context.Scope, operation.DocumentId, cancellationToken).ConfigureAwait(false)).Contains(key)))
        {
            return PushOutcome<TDocument>.Rejected(opId, PushErrorCodes.Forbidden);
        }

        if (_options.Validator?.Invoke(context, operation, current?.Document) is { } error)
        {
            return PushOutcome<TDocument>.Rejected(opId, error);
        }

        if (current is not null && operation.BaseVersion != current.Version)
        {
            return PushOutcome<TDocument>.Conflict(opId, current.Version, current.Document);
        }

        var version = nextVersion();
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO bs_documents (collection, scope, id, id_key, version, deleted, document) VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (collection, scope, id) DO UPDATE SET version = excluded.version, deleted = excluded.deleted, document = excluded.document
            """,
            cancellationToken,
            _options.Collection,
            context.Scope,
            operation.DocumentId,
            Encoding.BigEndianUnicode.GetBytes(operation.DocumentId),
            version,
            operation.Document.Deleted,
            json).ConfigureAwait(false);
        var accepted = Deserialize(json);
        if (_options.Readers is { } readers)
        {
            await WriteAccessAsync(connection, transaction, context.Scope, operation.DocumentId, accepted.Deleted ? null : readers(accepted), version, cancellationToken).ConfigureAwait(false);
        }

        return PushOutcome<TDocument>.Accepted(opId, version, accepted);
    }

    /// <inheritdoc />
    /// <remarks>With <paramref name="transaction"/> (an <see cref="NpgsqlTransaction"/>), the write enlists in it and the caller commits.</remarks>
    public Task<SyncPublishResult> UpsertAsync(string scope, TDocument document, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PublishAsync([scope], transaction, (write, ct) => PublishOneAsync(write, document, null, ct), cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncPublishResult> DeleteAsync(string scope, string id, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return PublishAsync([scope], transaction, (write, ct) => DeleteOneAsync(write, id, null, ct), cancellationToken);
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
                var stored = new Dictionary<string, (bool Deleted, string Json)>(StringComparer.Ordinal);
                await using (var read = new NpgsqlCommand("SELECT id, deleted, document FROM bs_documents WHERE collection = $1 AND scope = $2", write.Connection, write.Transaction))
                {
                    read.Parameters.Add(new() { Value = _options.Collection });
                    read.Parameters.Add(new() { Value = write.Scope });
                    await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        stored[reader.GetString(0)] = (reader.GetBoolean(1), reader.GetString(2));
                    }
                }

                var keep = new HashSet<string>(StringComparer.Ordinal);
                var result = SyncPublishResult.None;
                foreach (var document in list)
                {
                    if (!keep.Add(document.Id))
                    {
                        throw new ArgumentException($"The document '{document.Id}' is listed twice.", nameof(documents));
                    }

                    result = result.Add(await PublishOneAsync(write, document, stored.TryGetValue(document.Id, out var known) ? known : null, ct).ConfigureAwait(false));
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
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncPublishResult> PublishAsync(TDocument document, IEnumerable<string> scopes, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scopes);
        return PublishAsync([.. scopes], transaction, (write, ct) => PublishOneAsync(write, document, null, ct), cancellationToken);
    }

    // Feeds are locked in ordinal scope order, so concurrent fan-outs cannot deadlock on each other.
    private async Task<SyncPublishResult> PublishAsync(
        IReadOnlyList<string> scopes,
        DbTransaction? transaction,
        Func<PublishScope, CancellationToken, Task<SyncPublishResult>> publish,
        CancellationToken cancellationToken)
    {
        var ordered = scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (ordered.Any(scope => !SyncIds.IsValid(scope)))
        {
            throw new ArgumentException("Every scope must be a valid identifier.", nameof(scopes));
        }

        NpgsqlConnection? owned = null;
        NpgsqlTransaction? ownTransaction = null;
        NpgsqlConnection connection;
        NpgsqlTransaction active;
        if (transaction is not null)
        {
            active = transaction as NpgsqlTransaction
                ?? throw new ArgumentException("The transaction must be an Npgsql transaction.", nameof(transaction));
            connection = active.Connection ?? throw new ArgumentException("The transaction has already completed.", nameof(transaction));
        }
        else
        {
            owned = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            ownTransaction = await owned.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            connection = owned;
            active = ownTransaction;
        }

        try
        {
            var total = SyncPublishResult.None;
            var changed = new List<string>();
            foreach (var scope in ordered)
            {
                await ExecuteAsync(connection, active, "INSERT INTO bs_feeds (collection, scope, sequence, purged_through) VALUES ($1, $2, 0, 0) ON CONFLICT DO NOTHING", cancellationToken, _options.Collection, scope).ConfigureAwait(false);
                var sequence = await ScalarAsync<long>(connection, active, "SELECT sequence FROM bs_feeds WHERE collection = $1 AND scope = $2 FOR UPDATE", cancellationToken, _options.Collection, scope).ConfigureAwait(false);
                var start = sequence;
                total = total.Add(await publish(new PublishScope(connection, active, scope, () => ++sequence), cancellationToken).ConfigureAwait(false));
                if (sequence != start)
                {
                    await ExecuteAsync(connection, active, "UPDATE bs_feeds SET sequence = $3 WHERE collection = $1 AND scope = $2", cancellationToken, _options.Collection, scope, sequence).ConfigureAwait(false);
                    await ExecuteAsync(connection, active, "SELECT pg_notify($1, $2)", cancellationToken, Channel, $"{_instance}\u001f{_options.Collection}\u001f{scope}").ConfigureAwait(false);
                    changed.Add(scope);
                }
            }

            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                foreach (var scope in changed)
                {
                    _committed?.Invoke(new AuthorityCommit(scope, []));
                }
            }

            return total;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync().ConfigureAwait(false);
            }

            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Unchanged content (ignoring the timestamp) keeps its version, so rebuilding a projection adds no feed entries.
    private async Task<SyncPublishResult> PublishOneAsync(PublishScope write, TDocument document, (bool Deleted, string Json)? known, CancellationToken cancellationToken)
    {
        SyncIds.Validate(document.Id);
        var current = known ?? await ReadOneAsync(write, document.Id, cancellationToken).ConfigureAwait(false);
        var stored = Deserialize(Serialize(document));
        if (current is { } existing)
        {
            stored.UpdatedAt = Deserialize(existing.Json).UpdatedAt;
            if (Serialize(stored) == existing.Json
                && (_options.Readers is not { } readers || (await ReadGrantedAsync(write.Connection, write.Transaction, write.Scope, document.Id, cancellationToken).ConfigureAwait(false)).SetEquals(readers(stored))))
            {
                return new SyncPublishResult(0, 1, 0);
            }

            stored.UpdatedAt = document.UpdatedAt;
        }

        if (stored.UpdatedAt == default)
        {
            stored.UpdatedAt = _publisherClock.Now();
        }

        await WriteDocumentAsync(write, document.Id, stored, cancellationToken).ConfigureAwait(false);
        return new SyncPublishResult(1, 0, 0);
    }

    private async Task<SyncPublishResult> DeleteOneAsync(PublishScope write, string id, (bool Deleted, string Json)? known, CancellationToken cancellationToken)
    {
        var current = known ?? await ReadOneAsync(write, id, cancellationToken).ConfigureAwait(false);
        if (current is not { } existing)
        {
            return SyncPublishResult.None;
        }

        if (existing.Deleted)
        {
            return new SyncPublishResult(0, 1, 0);
        }

        var tombstone = Deserialize(existing.Json);
        tombstone.Deleted = true;
        tombstone.UpdatedAt = _publisherClock.Now();
        await WriteDocumentAsync(write, id, tombstone, cancellationToken).ConfigureAwait(false);
        return new SyncPublishResult(0, 0, 1);
    }

    private async Task<(bool Deleted, string Json)?> ReadOneAsync(PublishScope write, string id, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand("SELECT deleted, document FROM bs_documents WHERE collection = $1 AND scope = $2 AND id = $3", write.Connection, write.Transaction);
        read.Parameters.Add(new() { Value = _options.Collection });
        read.Parameters.Add(new() { Value = write.Scope });
        read.Parameters.Add(new() { Value = id });
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetBoolean(0), reader.GetString(1)) : null;
    }

    private async Task WriteDocumentAsync(PublishScope write, string id, TDocument document, CancellationToken cancellationToken)
    {
        var version = write.NextVersion();
        await ExecuteAsync(
            write.Connection,
            write.Transaction,
            """
            INSERT INTO bs_documents (collection, scope, id, id_key, version, deleted, document) VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (collection, scope, id) DO UPDATE SET version = excluded.version, deleted = excluded.deleted, document = excluded.document
            """,
            cancellationToken,
            _options.Collection,
            write.Scope,
            id,
            Encoding.BigEndianUnicode.GetBytes(id),
            version,
            document.Deleted,
            Serialize(document)).ConfigureAwait(false);
        if (_options.Readers is { } readers)
        {
            await WriteAccessAsync(write.Connection, write.Transaction, write.Scope, id, document.Deleted ? null : readers(document), version, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ADR-015: moves every current reader's access row to <paramref name="version"/> (granted) and marks principals that
    /// just lost access as revoked at the same version. A tombstone (<paramref name="readers"/> null) goes to everyone who
    /// could read the document.
    /// </summary>
    private async Task WriteAccessAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope, string id, IEnumerable<string>? readers, long version, CancellationToken cancellationToken)
    {
        var before = await ReadGrantedAsync(connection, transaction, scope, id, cancellationToken).ConfigureAwait(false);
        var after = readers is null ? before : readers.Where(SyncIds.IsValid).ToHashSet(StringComparer.Ordinal);
        var rows = after.Select(p => (Principal: p, Granted: true)).Concat(before.Where(p => !after.Contains(p)).Select(p => (Principal: p, Granted: false))).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO bs_document_access (collection, scope, principal_key, version, id, granted)
            SELECT $1, $2, p, $3, $4, g FROM unnest($5::text[], $6::boolean[]) AS t(p, g)
            ON CONFLICT (collection, scope, id, principal_key) DO UPDATE SET version = excluded.version, granted = excluded.granted
            """,
            connection,
            transaction);
        command.Parameters.Add(new() { Value = _options.Collection });
        command.Parameters.Add(new() { Value = scope });
        command.Parameters.Add(new() { Value = version });
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = rows.Select(r => r.Principal).ToArray() });
        command.Parameters.Add(new() { Value = rows.Select(r => r.Granted).ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> ReadGrantedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope, string id, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand(
            "SELECT principal_key FROM bs_document_access WHERE collection = $1 AND scope = $2 AND id = $3 AND granted",
            connection,
            transaction);
        read.Parameters.Add(new() { Value = _options.Collection });
        read.Parameters.Add(new() { Value = scope });
        read.Parameters.Add(new() { Value = id });
        var granted = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            granted.Add(reader.GetString(0));
        }

        return granted;
    }

    /// <summary>
    /// ADR-015: reads the caller's access rows in version order (full pages; cost follows what the caller can see). Revoked
    /// rows become removals for replicas that asked for them; others must resnapshot (<c>scope-changed</c>).
    /// </summary>
    private async Task<PullResult<TDocument>> PullMembershipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SyncCallContext context, PullRequest request, string? key, long since, int limit, long purgedThrough, CancellationToken cancellationToken)
    {
        var raw = new List<(long Version, bool Granted, string Id, long? DocumentVersion, string? Json)>();
        if (key is not null && SyncIds.IsValid(key))
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT a.version, a.granted, a.id, d.version, d.document
                FROM bs_document_access a
                LEFT JOIN bs_documents d ON a.granted AND d.collection = a.collection AND d.scope = a.scope AND d.id = a.id
                WHERE a.collection = $1 AND a.scope = $2 AND a.principal_key = $3 AND a.version > $4
                ORDER BY a.version
                LIMIT $5
                """,
                connection,
                transaction);
            command.Parameters.Add(new() { Value = _options.Collection });
            command.Parameters.Add(new() { Value = context.Scope });
            command.Parameters.Add(new() { Value = key });
            command.Parameters.Add(new() { Value = since });
            command.Parameters.Add(new() { Value = limit + 1 });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                raw.Add((reader.GetInt64(0), reader.GetBoolean(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var hasMore = raw.Count > limit;
        if (hasMore)
        {
            raw.RemoveAt(raw.Count - 1);
        }

        var acceptsRemovals = request.Features?.Contains(SyncFeatures.Removals, StringComparer.Ordinal) == true;
        var changes = new List<RemoteChange<TDocument>>();
        var removals = new List<string>();
        foreach (var row in raw)
        {
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
            ServerTime = _physical.NowMilliseconds(),
            RetentionHorizon = purgedThrough,
            Removals = acceptsRemovals ? removals : null,
        };
    }

    /// <inheritdoc />
    public async Task<StoredDocument<TDocument>?> GetAsync(SyncCallContext context, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(id);
        Scope(context);
        await using var command = _source.CreateCommand("SELECT version, document FROM bs_documents WHERE collection = $1 AND scope = $2 AND id = $3");
        command.Parameters.Add(new() { Value = _options.Collection });
        command.Parameters.Add(new() { Value = context.Scope });
        command.Parameters.Add(new() { Value = id });
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

        // COLLATE "C" orders by bytes of UTF-8, which is not UTF-16 ordinal order for every string; ids compare with
        // the id_key column (UTF-16BE bytes) instead.
        await using var command = _source.CreateCommand(
            "SELECT version, document FROM bs_documents WHERE collection = $1 AND scope = $2 AND NOT deleted AND ($3::bytea IS NULL OR id_key > $3) ORDER BY id_key LIMIT $4");
        command.Parameters.Add(new() { Value = _options.Collection });
        command.Parameters.Add(new() { Value = context.Scope });
        command.Parameters.Add(new() { Value = afterId is null ? DBNull.Value : Encoding.BigEndianUnicode.GetBytes(afterId), NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new() { Value = Math.Min(limit, _options.MaxPageSize) });
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
    public async Task<int> PurgeTombstonesAsync(string scope, long throughVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sequence = await ScalarAsync<long?>(connection, transaction, "SELECT sequence FROM bs_feeds WHERE collection = $1 AND scope = $2 FOR UPDATE", cancellationToken, _options.Collection, scope).ConfigureAwait(false);
        if (sequence is null)
        {
            return 0;
        }

        throughVersion = Math.Min(throughVersion, sequence.Value);
        await using var delete = new NpgsqlCommand("DELETE FROM bs_documents WHERE collection = $1 AND scope = $2 AND deleted AND version <= $3", connection, transaction);
        delete.Parameters.Add(new() { Value = _options.Collection });
        delete.Parameters.Add(new() { Value = scope });
        delete.Parameters.Add(new() { Value = throughVersion });
        var removed = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM bs_document_access a WHERE a.collection = $1 AND a.scope = $2 AND NOT EXISTS (SELECT 1 FROM bs_documents d WHERE d.collection = a.collection AND d.scope = a.scope AND d.id = a.id)",
            cancellationToken,
            _options.Collection,
            scope).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE bs_feeds SET purged_through = GREATEST(purged_through, $3) WHERE collection = $1 AND scope = $2", cancellationToken, _options.Collection, scope, throughVersion).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    /// <summary>Removes receipts of operations of <paramref name="scope"/> accepted at or below <paramref name="throughVersion"/>.</summary>
    /// <remarks>Keep receipts longer than any replica may stay offline; see docs/operations/disaster-recovery.md.</remarks>
    public async Task<int> PurgeReceiptsAsync(string scope, long throughVersion, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand("DELETE FROM bs_receipts WHERE collection = $1 AND scope = $2 AND kind = $3 AND version <= $4");
        command.Parameters.Add(new() { Value = _options.Collection });
        command.Parameters.Add(new() { Value = scope });
        command.Parameters.Add(new() { Value = (short)PushOutcomeKind.Accepted });
        command.Parameters.Add(new() { Value = throughVersion });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a new epoch after the database was restored from a backup: every checkpoint issued before gets
    /// <c>reset-required</c> (<c>epoch</c>), and every feed continues above <paramref name="versionFloor"/>, which must be
    /// at or above any version the lost history may have issued. Affects every collection in the database and every
    /// instance (they re-read the epoch on their next pull).
    /// </summary>
    public async Task BeginNewEpochAsync(long versionFloor, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(versionFloor);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE bs_feeds SET sequence = GREATEST(sequence, $1)", cancellationToken, versionFloor).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "UPDATE bs_meta SET value = $1 WHERE key = 'epoch'", cancellationToken, PostgreSqlSchema.NewEpoch()).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _epoch = null;
        await RefreshEpochAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, long>> GetFeedHeadsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand("SELECT scope, sequence FROM bs_feeds WHERE collection = $1");
        command.Parameters.Add(new() { Value = _options.Collection });
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
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long?>(connection, null, "SELECT sequence FROM bs_feeds WHERE collection = $1 AND scope = $2", cancellationToken, _options.Collection, scope).ConfigureAwait(false) ?? 0;
    }

    /// <inheritdoc />
    /// <remarks>Raised for commits by this instance and, while there are subscribers, by other instances sharing the database (those carry no ids).</remarks>
    public event Action<AuthorityCommit>? Committed
    {
        add
        {
            lock (_listenGate)
            {
                _committed += value;
                if (_listener is null && _committed is not null)
                {
                    _listening = new CancellationTokenSource();
                    _listener = Task.Run(() => ListenAsync(_listening.Token));
                }
            }
        }

        remove
        {
            lock (_listenGate)
            {
                _committed -= value;
            }
        }
    }

    /// <summary>Stops listening for commits by other instances.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? listener;
        lock (_listenGate)
        {
            _listening?.Cancel();
            listener = _listener;
        }

        if (listener is not null)
        {
            try
            {
                await listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ListenAsync(CancellationToken stopping)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _source.OpenConnectionAsync(stopping).ConfigureAwait(false);
                connection.Notification += (_, e) =>
                {
                    var parts = e.Payload.Split('\u001f');
                    if (parts.Length == 3 && parts[0] != _instance && parts[1] == _options.Collection)
                    {
                        _committed?.Invoke(new AuthorityCommit(parts[2], []));
                    }
                };
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stopping).ConfigureAwait(false);
                }

                delay = TimeSpan.FromMilliseconds(200);
                while (!stopping.IsCancellationRequested)
                {
                    await connection.WaitAsync(stopping).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is NpgsqlException or IOException or TimeoutException)
            {
                // Hints are best effort (protocol §8): reconnect with backoff; clients reconcile on their own schedule.
                await Task.Delay(delay, stopping).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }
    }

    private void Scope(SyncCallContext context)
    {
        if (!SyncIds.IsValid(context.Scope))
        {
            throw new SyncTransportException(SyncErrorCodes.Forbidden, "No valid scope for the caller.", isTransient: false);
        }
    }

    private async Task RefreshEpochAsync(CancellationToken cancellationToken)
    {
        // Another instance may have started a new epoch; read it at most once per pull (a cheap primary-key lookup).
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _epoch = await ScalarAsync<string?>(connection, null, "SELECT value FROM bs_meta WHERE key = 'epoch'", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Bsync schema has no epoch.");
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

    private static string Fingerprint(PushOperation<TDocument> operation, string json)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture, $"{operation.DocumentId.Length}:{operation.DocumentId}|{operation.BaseVersion}|{operation.Group}|{operation.GroupSize}|{json}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private Checkpoint FormatCheckpoint(SyncCallContext context, long position) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{Epoch}~{ScopeHash(context)}:{position}"));

    // A checkpoint names a position in one feed: bind the collection, the scope and what the caller may see, so a
    // checkpoint from another feed of the same database is never resumed.
    // With membership a checkpoint names a position in one principal's view, so the principal is bound too (only then,
    // so checkpoints of authorities without membership are unchanged).
    private string ScopeHash(SyncCallContext context) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{_options.Collection}{context.Scope}{_options.ScopeFingerprint?.Invoke(context)}"
            + (_options.PrincipalKey is { } principalKey ? $"{principalKey(context)}" : string.Empty))))[..16];

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

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new() { Value = parameter ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new() { Value = parameter ?? DBNull.Value });
        }

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
    }

    private sealed record PublishScope(NpgsqlConnection Connection, NpgsqlTransaction Transaction, string Scope, Func<long> NextVersion);
}
