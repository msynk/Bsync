using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Bsync.Server.SqlServer.Workflows;

/// <summary>Configured contributing table with a single bounded string, integer or GUID primary key.</summary>
public sealed record ChangeTrackingSourceTable(string Schema, string Table, string KeyColumn);
/// <summary>A changed source key, including deleted keys whose parent mapping must be supplied by the host.</summary>
public sealed record ChangeTrackingSourceKey(string Schema, string Table, string Key, bool Deleted);
/// <summary>A sealed current-state capture boundary. It is not an immutable business-event history.</summary>
public sealed record ChangeTrackingBatch(Guid Id, long? LowerVersion, long UpperVersion, bool InitialSnapshot,
    IReadOnlyList<ChangeTrackingSourceKey> Keys);

/// <summary>Read-only operational observation. Version distance is not elapsed time or an event count.</summary>
public sealed record ChangeTrackingHealth(DateTimeOffset ObservedUtc, long? PublishedVersion, long? CurrentVersion,
    Guid? PendingBatchId, int PendingKeys, DateTimeOffset? PendingSinceUtc, long? MinimumValidVersion,
    bool RequiresResnapshot, string? Reason)
{
    /// <summary>Last failed pending publication's exception type. No source content or exception message is retained.</summary>
    public string? PendingFailure { get; init; }
}

/// <summary>
/// Optional bounded SQL Server Change Tracking capture with durable sealed staging and transactional publication.
/// Source, staging and destination must share one database. The host supplies canonical projection/membership rules,
/// a restore epoch from outside the source database, and atomic full-snapshot publication. No database features are enabled automatically.
/// </summary>
public sealed partial class SqlServerChangeTrackingCapture
{
    private readonly string _connectionString, _stream, _epoch, _schema;
    private readonly ChangeTrackingSourceTable[] _tables;
    private readonly int _maximumKeys;
    /// <summary>Explicit deployment migration for staging and cursors; does not enable Change Tracking or snapshot isolation.</summary>
    public static string MigrationSql { get; } = WorkflowSchema.Migration("change-tracking", """
        IF OBJECT_ID(N'dbo.BsyncCaptureState', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.BsyncCaptureState(Stream char(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Epoch nvarchar(256) NOT NULL, SchemaId nvarchar(256) NOT NULL, Boundary bigint NULL, Pending uniqueidentifier NULL, SourceStamp char(64) NULL);
            CREATE TABLE dbo.BsyncCaptureBatches(Id uniqueidentifier NOT NULL PRIMARY KEY, Stream char(64) NOT NULL,
                Epoch nvarchar(256) NOT NULL, SchemaId nvarchar(256) NOT NULL, LowerVersion bigint NULL,
                UpperVersion bigint NOT NULL, Initial bit NOT NULL, Sealed bit NOT NULL, Applied bit NOT NULL DEFAULT 0);
            CREATE TABLE dbo.BsyncCaptureKeys(Batch uniqueidentifier NOT NULL, Ordinal int NOT NULL,
                SourceSchema nvarchar(128) NOT NULL, SourceTable nvarchar(128) NOT NULL, SourceKey nvarchar(256) NOT NULL,
                Deleted bit NOT NULL, PRIMARY KEY(Batch,Ordinal), FOREIGN KEY(Batch) REFERENCES dbo.BsyncCaptureBatches(Id));
        END;
        IF COL_LENGTH(N'dbo.BsyncCaptureBatches',N'StagedUtc') IS NULL
            ALTER TABLE dbo.BsyncCaptureBatches ADD StagedUtc datetimeoffset NULL;
        IF COL_LENGTH(N'dbo.BsyncCaptureBatches',N'Failure') IS NULL
            ALTER TABLE dbo.BsyncCaptureBatches ADD Failure varchar(128) NULL;
        IF OBJECT_ID(N'dbo.BsyncCaptureGenerations', N'U') IS NULL
            CREATE TABLE dbo.BsyncCaptureGenerations(Id uniqueidentifier NOT NULL PRIMARY KEY,
                Stream char(64) NOT NULL, Epoch nvarchar(256) NOT NULL, SchemaId nvarchar(256) NOT NULL,
                SourceStamp char(64) NOT NULL, Batch uniqueidentifier NOT NULL, NextOrdinal int NOT NULL,
                TotalKeys int NOT NULL, MaximumKeys int NOT NULL, Boundary bigint NOT NULL,
                Status varchar(16) NOT NULL, UpdatedUtc datetimeoffset NOT NULL,
                Failure varchar(128) NULL, FOREIGN KEY(Batch) REFERENCES dbo.BsyncCaptureBatches(Id));
        IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.BsyncCaptureGenerations') AND name='IX_BsyncCaptureGeneration_Stream')
            CREATE UNIQUE INDEX IX_BsyncCaptureGeneration_Stream ON dbo.BsyncCaptureGenerations(Stream) WHERE Status='building';
        """, 2);

    /// <summary>
    /// Configures an explicitly named stream. Epoch must change after restore/history loss under an operational procedure;
    /// numerically plausible CT versions cannot establish continuity. Schema identity binds projection and membership rules.
    /// Batches exceeding maximumKeys (1..10000) fail without advancing state; choose a separately reviewed partitioning plan.
    /// </summary>
    public SqlServerChangeTrackingCapture(string connectionString, string stream, string sourceEpoch, string projectionSchema,
        IReadOnlyList<ChangeTrackingSourceTable> tables, int maximumKeys = 10000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (!SyncIds.IsValid(stream) || !SyncIds.IsValid(sourceEpoch) || !SyncIds.IsValid(projectionSchema)) throw new ArgumentException("Invalid capture identity.");
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count is < 1 or > 32 || maximumKeys is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(tables));
        _tables = tables.ToArray();
        foreach (var table in _tables) { _ = Quote(table.Schema); _ = Quote(table.Table); _ = Quote(table.KeyColumn); }
        if (_tables.Select(t => (t.Schema, t.Table)).Distinct().Count() != _tables.Length) throw new ArgumentException("Duplicate source table.");
        _connectionString = connectionString;
        _stream = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stream)));
        _epoch = sourceEpoch;
        // Configuration changes cannot silently reuse a cursor, even if a host forgets to change its schema label.
        _schema = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projectionSchema + "\n" + string.Join("\n", _tables.Select(t => $"{t.Schema}.{t.Table}.{t.KeyColumn}")))));
        _maximumKeys = maximumKeys;
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Source identifiers use 1..128 ASCII letters, digits or underscore.");
        return "[" + value + "]";
    }
    private async Task Lock(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await WorkflowSchema.Validate(connection, transaction, "change-tracking", ct).ConfigureAwait(false);
        await using var command = new SqlCommand("DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0; SELECT @result;", connection, transaction);
        command.Parameters.AddWithValue("@resource", "Bsync.capture/" + _stream);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) < 0) throw new IOException("Capture stream is owned by another worker; retry later.");
    }
    private void Identity(SqlCommand command)
    {
        command.Parameters.AddWithValue("@stream", _stream); command.Parameters.AddWithValue("@epoch", _epoch); command.Parameters.AddWithValue("@schema", _schema);
    }
    private async Task<(long? Boundary, Guid? Pending, string? Stamp)> State(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await RefuseBuildingGeneration(connection, transaction, ct).ConfigureAwait(false);
        await using var command = new SqlCommand("SELECT Epoch,SchemaId,Boundary,Pending,SourceStamp FROM dbo.BsyncCaptureState WHERE Stream=@stream", connection, transaction);
        Identity(command);
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(0) != _epoch || reader.GetString(1) != _schema) throw new InvalidOperationException("Capture epoch/schema changed; explicit resnapshot required.");
                return (reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }
        await using var create = new SqlCommand("INSERT dbo.BsyncCaptureState(Stream,Epoch,SchemaId) VALUES(@stream,@epoch,@schema)", connection, transaction);
        Identity(create); await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false); return (null, null, null);
    }

    private async Task<string> SourceStamp(SqlConnection connection, SqlTransaction tx, CancellationToken ct)
    {
        var identity = new StringBuilder();
        foreach (var table in _tables)
        {
            await using var command = new SqlCommand("""
                SELECT t.object_id,ct.begin_version,CONVERT(nvarchar(50),t.create_date,126),CONVERT(nvarchar(50),t.modify_date,126)
                FROM sys.tables t JOIN sys.change_tracking_tables ct ON ct.object_id=t.object_id WHERE t.object_id=OBJECT_ID(@table)
                """, connection, tx);
            command.Parameters.AddWithValue("@table", table.Schema + "." + table.Table);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new NotSupportedException("A contributing table is missing or untracked.");
            for (var i = 0; i < 4; i++) identity.Append(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    /// <summary>
    /// Observes lag, pending batch age and retention/epoch prerequisites without creating a stream, advancing a cursor,
    /// acknowledging work or enabling features. Historical batches without an observed timestamp report null age.
    /// A null published version means initial snapshot is needed. Runtime/schema/permission errors remain explicit.
    /// </summary>
    public async Task<ChangeTrackingHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await WorkflowSchema.Validate(connection, null, "change-tracking", cancellationToken).ConfigureAwait(false);
        bool snapshot;
        await using (var prerequisite = new SqlCommand("SELECT snapshot_isolation_state FROM sys.databases WHERE database_id=DB_ID()", connection))
            snapshot = Convert.ToInt32(await prerequisite.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
        await using var tx = connection.BeginTransaction(snapshot ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted);
        long? boundary = null, current = null, minimum = null; Guid? pending = null; DateTimeOffset? since = null;
        var pendingKeys = 0; string? reason = snapshot ? null : "snapshot-isolation-unavailable"; string? stamp = null, failure = null;
        await using (var command = new SqlCommand("SELECT Epoch,SchemaId,Boundary,Pending,SourceStamp FROM dbo.BsyncCaptureState WHERE Stream=@stream", connection, tx))
        {
            Identity(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) != _epoch || reader.GetString(1) != _schema) reason = "epoch-or-schema-changed";
                if (!reader.IsDBNull(2)) boundary = reader.GetInt64(2);
                if (!reader.IsDBNull(3)) pending = reader.GetGuid(3);
                if (!reader.IsDBNull(4)) stamp = reader.GetString(4);
            }
        }
        await using (var command = new SqlCommand("SELECT CHANGE_TRACKING_CURRENT_VERSION()", connection, tx))
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long version) current = version;
        if (current is null) reason ??= "change-tracking-unavailable";
        if (boundary > current) reason ??= "source-version-regressed";
        foreach (var table in _tables)
        {
            await using var command = new SqlCommand("SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@table))", connection, tx);
            command.Parameters.AddWithValue("@table", table.Schema + "." + table.Table);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long valid)
                minimum = Math.Max(minimum ?? valid, valid);
            else reason ??= "source-unavailable";
        }
        if (boundary < minimum) reason ??= "retention-expired";
        if (stamp is not null && reason is null && stamp != await SourceStamp(connection, tx, cancellationToken).ConfigureAwait(false)) reason = "source-generation-changed";
        if (pending is { } id)
        {
            await using var command = new SqlCommand("SELECT StagedUtc,(SELECT COUNT(*) FROM dbo.BsyncCaptureKeys WHERE Batch=@id),Failure FROM dbo.BsyncCaptureBatches WHERE Id=@id AND Sealed=1 AND Applied=0", connection, tx);
            command.Parameters.AddWithValue("@id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("Pending sealed capture batch is missing.");
            since = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0); pendingKeys = reader.GetInt32(1);
            failure = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        return new(DateTimeOffset.UtcNow, boundary, current, pending, pendingKeys, since, minimum,
            reason is not null && (boundary is not null || reason == "epoch-or-schema-changed"), reason) { PendingFailure = failure };
    }

    /// <summary>
    /// Captures all configured sources within one snapshot and seals keys atomically. A prior pending batch is replayed,
    /// never replaced. Initial snapshots enumerate all source keys. Every table's retention/primary-key prerequisites
    /// are validated before enumeration. Cancellation/overflow rolls back staging and preserves the published boundary.
    /// </summary>
    public async Task<ChangeTrackingBatch> StageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var prerequisite = new SqlCommand("SELECT snapshot_isolation_state FROM sys.databases WHERE database_id=DB_ID()", connection))
            if (Convert.ToInt32(await prerequisite.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1) throw new InvalidOperationException("Snapshot isolation must be enabled by deployment.");
        await using var tx = connection.BeginTransaction(IsolationLevel.Snapshot);
        await Lock(connection, tx, cancellationToken).ConfigureAwait(false);
        var state = await State(connection, tx, cancellationToken).ConfigureAwait(false);
        var stamp = await SourceStamp(connection, tx, cancellationToken).ConfigureAwait(false);
        if (state.Stamp is not null && state.Stamp != stamp) throw new InvalidOperationException("Source table/key/tracking generation changed; explicit resnapshot required.");
        if (state.Pending is { } pending) return await ReadBatch(connection, tx, pending, cancellationToken).ConfigureAwait(false);
        foreach (var table in _tables)
        {
            await using var check = new SqlCommand("""
                SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@table)),
                    (SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                     WHERE i.object_id=OBJECT_ID(@table) AND i.is_primary_key=1),
                    (SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                     JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                     WHERE i.object_id=OBJECT_ID(@table) AND i.is_primary_key=1 AND c.name=@key
                     AND (TYPE_NAME(c.system_type_id) IN ('int','bigint','uniqueidentifier')
                       OR (TYPE_NAME(c.system_type_id)='nvarchar' AND c.max_length BETWEEN 1 AND 512)
                       OR (TYPE_NAME(c.system_type_id)='varchar' AND c.max_length BETWEEN 1 AND 256)))
                """, connection, tx);
            check.Parameters.AddWithValue("@table", table.Schema + "." + table.Table); check.Parameters.AddWithValue("@key", table.KeyColumn);
            await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reader.IsDBNull(0) || reader.GetInt32(1) != 1 || reader.GetInt32(2) != 1) throw new NotSupportedException("A contributing table is unavailable, untracked or has an unsupported primary key.");
            if (state.Boundary is { } lower && lower < reader.GetInt64(0)) throw new InvalidOperationException("Change Tracking retention invalidated the cursor; explicit resnapshot required.");
        }
        long upper;
        await using (var high = new SqlCommand("SELECT CHANGE_TRACKING_CURRENT_VERSION()", connection, tx))
            upper = Convert.ToInt64(await high.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (state.Boundary > upper) throw new InvalidOperationException("Source version regressed; restore epoch transition required.");
        var keys = new List<ChangeTrackingSourceKey>();
        foreach (var table in _tables)
        {
            var tableName = Quote(table.Schema) + "." + Quote(table.Table); var key = Quote(table.KeyColumn);
            var sql = state.Boundary is null ? $"SELECT CONVERT(nvarchar(256),{key}),CAST(0 AS bit) FROM {tableName}"
                : $"SELECT CONVERT(nvarchar(256),{key}),CAST(CASE WHEN SYS_CHANGE_OPERATION='D' THEN 1 ELSE 0 END AS bit) FROM CHANGETABLE(CHANGES {tableName},@lower) c WHERE SYS_CHANGE_VERSION<=@upper";
            await using var read = new SqlCommand(sql, connection, tx);
            if (state.Boundary is { } lower) { read.Parameters.AddWithValue("@lower", lower); read.Parameters.AddWithValue("@upper", upper); }
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (keys.Count == _maximumKeys) throw new InvalidOperationException("Capture exceeds its declared key budget; no cursor advanced.");
                keys.Add(new(table.Schema, table.Table, reader.GetString(0), reader.GetBoolean(1)));
            }
        }
        var batch = new ChangeTrackingBatch(Guid.NewGuid(), state.Boundary, upper, state.Boundary is null, keys.AsReadOnly());
        await using (var insert = new SqlCommand("""
            INSERT dbo.BsyncCaptureBatches(Id,Stream,Epoch,SchemaId,LowerVersion,UpperVersion,Initial,Sealed,StagedUtc)
            VALUES(@id,@stream,@epoch,@schema,@lower,@upper,@initial,0,SYSUTCDATETIME())
            """, connection, tx))
        {
            Identity(insert); insert.Parameters.AddWithValue("@id", batch.Id); insert.Parameters.AddWithValue("@lower", (object?)batch.LowerVersion ?? DBNull.Value);
            insert.Parameters.AddWithValue("@upper", upper); insert.Parameters.AddWithValue("@initial", batch.InitialSnapshot);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            await using var insert = new SqlCommand("INSERT dbo.BsyncCaptureKeys VALUES(@id,@ordinal,@schema,@table,@key,@deleted)", connection, tx);
            insert.Parameters.AddWithValue("@id", batch.Id); insert.Parameters.AddWithValue("@ordinal", i);
            insert.Parameters.AddWithValue("@schema", key.Schema); insert.Parameters.AddWithValue("@table", key.Table);
            insert.Parameters.AddWithValue("@key", key.Key); insert.Parameters.AddWithValue("@deleted", key.Deleted);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var seal = new SqlCommand("UPDATE dbo.BsyncCaptureBatches SET Sealed=1 WHERE Id=@id; UPDATE dbo.BsyncCaptureState SET Pending=@id,SourceStamp=@stamp WHERE Stream=@stream", connection, tx))
        { seal.Parameters.AddWithValue("@stamp", stamp); seal.Parameters.AddWithValue("@id", batch.Id); seal.Parameters.AddWithValue("@stream", _stream); await seal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return batch;
    }

    private async Task<ChangeTrackingBatch> ReadBatch(SqlConnection connection, SqlTransaction tx, Guid id, CancellationToken ct)
    {
        long? lower; long upper; bool initial;
        await using (var read = new SqlCommand("SELECT LowerVersion,UpperVersion,Initial,Sealed,Epoch,SchemaId FROM dbo.BsyncCaptureBatches WHERE Id=@id AND Stream=@stream", connection, tx))
        {
            read.Parameters.AddWithValue("@id", id); read.Parameters.AddWithValue("@stream", _stream);
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || !reader.GetBoolean(3) || reader.GetString(4) != _epoch || reader.GetString(5) != _schema)
                throw new InvalidDataException("Missing, unsealed or incompatible capture batch; evidence retained.");
            lower = reader.IsDBNull(0) ? null : reader.GetInt64(0); upper = reader.GetInt64(1); initial = reader.GetBoolean(2);
        }
        var keys = new List<ChangeTrackingSourceKey>();
        await using var command = new SqlCommand("SELECT SourceSchema,SourceTable,SourceKey,Deleted FROM dbo.BsyncCaptureKeys WHERE Batch=@id ORDER BY Ordinal", connection, tx);
        command.Parameters.AddWithValue("@id", id);
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (keys.Count == _maximumKeys) throw new InvalidDataException("Staged batch exceeds configured bounds.");
                keys.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
            }
        return new(id, lower, upper, initial, keys.AsReadOnly());
    }

    /// <summary>
    /// Applies the current sealed batch and advances its cursor in ONE caller-visible database transaction. The callback
    /// must acquire publication locks before domain reads, reread current canonical state (never replay stale payloads),
    /// map deleted/related keys explicitly, and for InitialSnapshot build and atomically replace a COMPLETE scope/generation,
    /// including deletion/membership reconciliation. It must not publish a partial enumeration or perform external I/O.
    /// An exception rolls back publication, marker and cursor together. Historical sealed batches are retained as evidence.
    /// </summary>
    public async Task<bool> PublishAsync(Func<SqlConnection, SqlTransaction, ChangeTrackingBatch, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publish);
        Guid? attempted = null;
        try
        {
            return await PublishCoreAsync((c, t, batch, ct) => { attempted = batch.Id; return publish(c, t, batch, ct); }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (attempted is { } id)
            {
                try
                {
                    await using var c = new SqlConnection(_connectionString); await c.OpenAsync().ConfigureAwait(false);
                    await using var command = new SqlCommand("UPDATE dbo.BsyncCaptureBatches SET Failure=@failure WHERE Id=@id AND Applied=0 AND EXISTS(SELECT 1 FROM dbo.BsyncCaptureState WHERE Stream=@stream AND Pending=@id)", c);
                    command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@stream", _stream);
                    command.Parameters.AddWithValue("@failure", error.GetType().Name.Length <= 128 ? error.GetType().Name : "ProjectionFailure");
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch (SqlException) { /* Retain the original error if diagnostics cannot be persisted. */ }
            }
            throw;
        }
    }

    private async Task<bool> PublishCoreAsync(Func<SqlConnection, SqlTransaction, ChangeTrackingBatch, CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Canonical reads and full-scope replacement must share a stable domain view. Serializable range locks
        // cover contributing reads until publication commits; source writers then appear in the next CT delta.
        await using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        await Lock(connection, tx, cancellationToken).ConfigureAwait(false);
        var state = await State(connection, tx, cancellationToken).ConfigureAwait(false);
        if (state.Pending is not { } pending) return false;
        if (state.Stamp != await SourceStamp(connection, tx, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Source generation changed before publication; resnapshot required.");
        var batch = await ReadBatch(connection, tx, pending, cancellationToken).ConfigureAwait(false);
        if (batch.LowerVersion != state.Boundary) throw new InvalidDataException("Out-of-order capture publication refused.");
        await publish(connection, tx, batch, cancellationToken).ConfigureAwait(false);
        await using var commit = new SqlCommand("UPDATE dbo.BsyncCaptureBatches SET Applied=1 WHERE Id=@id; UPDATE dbo.BsyncCaptureState SET Boundary=@upper,Pending=NULL WHERE Stream=@stream AND Pending=@id", connection, tx);
        commit.Parameters.AddWithValue("@id", batch.Id); commit.Parameters.AddWithValue("@upper", batch.UpperVersion); commit.Parameters.AddWithValue("@stream", _stream);
        await commit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        WorkflowSchema.Record("capture", "published");
        return true;
    }

    /// <summary>
    /// Explicit operational resnapshot transition after retention expiry, restore or schema change. Requires the
    /// expected previous epoch; retains all previous sealed batches as evidence and leaves published content intact.
    /// The next StageAsync captures a complete new candidate; PublishAsync must atomically replace the full scope.
    /// Coordinate source restore and all publication routes before invoking this method.
    /// </summary>
    public async Task BeginResnapshotAsync(string expectedPreviousEpoch, CancellationToken cancellationToken = default)
    {
        if (!SyncIds.IsValid(expectedPreviousEpoch)) throw new ArgumentException("Invalid previous epoch.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = connection.BeginTransaction();
        await Lock(connection, tx, cancellationToken).ConfigureAwait(false);
        await RefuseBuildingGeneration(connection, tx, cancellationToken).ConfigureAwait(false);
        await using var update = new SqlCommand("UPDATE dbo.BsyncCaptureState SET Epoch=@epoch,SchemaId=@schema,Boundary=NULL,Pending=NULL,SourceStamp=NULL WHERE Stream=@stream AND Epoch=@previous", connection, tx);
        Identity(update); update.Parameters.AddWithValue("@previous", expectedPreviousEpoch);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidOperationException("Capture epoch changed or is absent; resnapshot transition refused.");
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
