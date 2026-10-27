using System.Data;
using Microsoft.Data.SqlClient;

namespace Bsync.Server.SqlServer.Workflows;

/// <summary>Durable isolated-generation progress. Completed pages are safe to resume after restart.</summary>
public sealed record ChangeTrackingGeneration(Guid Id, Guid BatchId, int CompletedKeys, int TotalKeys,
    long Boundary, string Status, DateTimeOffset UpdatedUtc, string? Failure);

public sealed partial class SqlServerChangeTrackingCapture
{
    private async Task RefuseBuildingGeneration(SqlConnection c, SqlTransaction t, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.BsyncCaptureGenerations WHERE Stream=@stream AND Status='building'", c, t);
        command.Parameters.AddWithValue("@stream", _stream);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
            throw new InvalidOperationException("An isolated resnapshot is in progress; use its generation workflow or explicitly abandon it.");
    }

    /// <summary>Observes the current building generation without creating work. Failure contains only an exception type, never source data.</summary>
    public async Task<ChangeTrackingGeneration?> GetGenerationAsync(CancellationToken cancellationToken = default)
    {
        await using var c = new SqlConnection(_connectionString);
        await c.OpenAsync(cancellationToken).ConfigureAwait(false);
        await WorkflowSchema.Validate(c, null, "change-tracking", cancellationToken).ConfigureAwait(false);
        return await ReadGeneration(c, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChangeTrackingGeneration?> ReadGeneration(SqlConnection c, SqlTransaction? t, Guid? id, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            SELECT Id,Batch,NextOrdinal,TotalKeys,Boundary,Status,UpdatedUtc,Failure,Epoch,SchemaId
            FROM dbo.BsyncCaptureGenerations WHERE Stream=@stream AND
              ((@id IS NULL AND Status='building') OR Id=@id)
            """, c, t);
        command.Parameters.AddWithValue("@stream", _stream);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = (object?)id ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        if (reader.GetString(8) != _epoch || reader.GetString(9) != _schema)
            throw new InvalidOperationException("Generation epoch/schema mismatch; retain and explicitly abandon it using its original configuration.");
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4),
            reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private async Task ValidateGenerationSources(SqlConnection c, SqlTransaction t, long? lower, CancellationToken ct)
    {
        foreach (var table in _tables)
        {
            await using var command = new SqlCommand("""
                SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@table)),
                  (SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                   WHERE i.object_id=OBJECT_ID(@table) AND i.is_primary_key=1),
                  (SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                   JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                   WHERE i.object_id=OBJECT_ID(@table) AND i.is_primary_key=1 AND c.name=@key AND
                   (TYPE_NAME(c.system_type_id) IN ('int','bigint','uniqueidentifier') OR
                    (TYPE_NAME(c.system_type_id)='nvarchar' AND c.max_length BETWEEN 1 AND 512) OR
                    (TYPE_NAME(c.system_type_id)='varchar' AND c.max_length BETWEEN 1 AND 256)))
                """, c, t);
            command.Parameters.AddWithValue("@table", table.Schema + "." + table.Table);
            command.Parameters.AddWithValue("@key", table.KeyColumn);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            if (reader.IsDBNull(0) || reader.GetInt32(1) != 1 || reader.GetInt32(2) != 1)
                throw new NotSupportedException("A source table is unavailable, untracked or has an unsupported primary key.");
            if (lower < reader.GetInt64(0)) throw new InvalidOperationException("Generation retention expired; abandon and restart the full generation.");
        }
    }

    private static async Task<long> CurrentVersion(SqlConnection c, SqlTransaction t, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT CHANGE_TRACKING_CURRENT_VERSION()", c, t);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is long value ? value
            : throw new InvalidOperationException("Change Tracking is unavailable.");
    }

    // SQL-to-SQL staging holds no full key inventory in managed memory. A sealed page is bounded separately at read time.
    private async Task<(Guid Batch, long Upper, int Count)> SealGenerationKeys(SqlConnection c, SqlTransaction t,
        long? lower, int maximumKeys, CancellationToken ct)
    {
        await ValidateGenerationSources(c, t, lower, ct).ConfigureAwait(false);
        var upper = await CurrentVersion(c, t, ct).ConfigureAwait(false);
        if (lower > upper) throw new InvalidOperationException("Source version regressed; an external restore epoch transition is required.");
        var id = Guid.NewGuid();
        await using (var insert = new SqlCommand("""
            INSERT dbo.BsyncCaptureBatches(Id,Stream,Epoch,SchemaId,LowerVersion,UpperVersion,Initial,Sealed,StagedUtc)
            VALUES(@id,@stream,@epoch,@schema,@lower,@upper,@initial,0,SYSUTCDATETIME())
            """, c, t))
        {
            Identity(insert); insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@lower", (object?)lower ?? DBNull.Value);
            insert.Parameters.AddWithValue("@upper", upper); insert.Parameters.AddWithValue("@initial", lower is null);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        var count = 0;
        foreach (var table in _tables)
        {
            var name = Quote(table.Schema) + "." + Quote(table.Table); var key = Quote(table.KeyColumn);
            var source = lower is null ? name : $"CHANGETABLE(CHANGES {name},@lower) changes";
            var deleted = lower is null ? "CAST(0 AS bit)" : "CAST(CASE WHEN SYS_CHANGE_OPERATION='D' THEN 1 ELSE 0 END AS bit)";
            var filter = lower is null ? "" : "WHERE SYS_CHANGE_VERSION<=@upper";
            await using var insert = new SqlCommand($"""
                INSERT dbo.BsyncCaptureKeys(Batch,Ordinal,SourceSchema,SourceTable,SourceKey,Deleted)
                SELECT TOP (@remaining) @batch,@offset+CONVERT(int,ROW_NUMBER() OVER(ORDER BY {key}))-1,@schema,@table,
                    CONVERT(nvarchar(256),{key}),{deleted} FROM {source} {filter} ORDER BY {key};
                SELECT @@ROWCOUNT;
                """, c, t);
            insert.Parameters.AddWithValue("@remaining", maximumKeys - count + 1);
            insert.Parameters.AddWithValue("@batch", id); insert.Parameters.AddWithValue("@offset", count);
            insert.Parameters.AddWithValue("@schema", table.Schema); insert.Parameters.AddWithValue("@table", table.Table);
            insert.Parameters.AddWithValue("@lower", (object?)lower ?? DBNull.Value); insert.Parameters.AddWithValue("@upper", upper);
            count += Convert.ToInt32(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (count > maximumKeys) throw new InvalidOperationException("Generation exceeds its durable key budget; no progress committed.");
        }
        await using var seal = new SqlCommand("UPDATE dbo.BsyncCaptureBatches SET Sealed=1 WHERE Id=@id", c, t);
        seal.Parameters.AddWithValue("@id", id); await seal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return (id, upper, count);
    }

    /// <summary>
    /// Seals a full key inventory in one source snapshot, leaving the active projection and cursor intact. The host must
    /// close older capture workers before applying migration 2. A repeated call resumes the same building generation.
    /// expectedPreviousEpoch must match existing state (null only for a new stream). maximumKeys bounds durable staging,
    /// not page memory; range 1..1,000,000. Source, staging and destination remain in the same database.
    /// </summary>
    public async Task<ChangeTrackingGeneration> BeginGenerationAsync(string? expectedPreviousEpoch = null,
        int maximumKeys = 1000000, CancellationToken cancellationToken = default)
    {
        if (maximumKeys is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(maximumKeys));
        if (expectedPreviousEpoch is not null && !SyncIds.IsValid(expectedPreviousEpoch)) throw new ArgumentException("Invalid previous epoch.");
        await using var c = new SqlConnection(_connectionString); await c.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var t = c.BeginTransaction(IsolationLevel.Snapshot);
        await Lock(c, t, cancellationToken).ConfigureAwait(false);
        if (await ReadGeneration(c, t, null, cancellationToken).ConfigureAwait(false) is { } existing) return existing;
        await using (var check = new SqlCommand("SELECT Epoch FROM dbo.BsyncCaptureState WHERE Stream=@stream", c, t))
        {
            check.Parameters.AddWithValue("@stream", _stream);
            if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string != expectedPreviousEpoch)
                throw new InvalidOperationException("Previous epoch does not match; generation transition refused.");
        }
        var stamp = await SourceStamp(c, t, cancellationToken).ConfigureAwait(false);
        var batch = await SealGenerationKeys(c, t, null, maximumKeys, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        await using (var insert = new SqlCommand("""
            INSERT dbo.BsyncCaptureGenerations(Id,Stream,Epoch,SchemaId,SourceStamp,Batch,NextOrdinal,TotalKeys,MaximumKeys,Boundary,Status,UpdatedUtc)
            VALUES(@id,@stream,@epoch,@schema,@stamp,@batch,0,@count,@maximum,@upper,'building',SYSUTCDATETIME())
            """, c, t))
        {
            Identity(insert); insert.Parameters.AddWithValue("@id", id); insert.Parameters.AddWithValue("@stamp", stamp);
            insert.Parameters.AddWithValue("@batch", batch.Batch); insert.Parameters.AddWithValue("@count", batch.Count);
            insert.Parameters.AddWithValue("@maximum", maximumKeys); insert.Parameters.AddWithValue("@upper", batch.Upper);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var result = (await ReadGeneration(c, t, id, cancellationToken).ConfigureAwait(false))!;
        await t.CommitAsync(cancellationToken).ConfigureAwait(false);
        WorkflowSchema.Record("capture_generation", "sealed"); return result;
    }

    private async Task CheckGenerationStamp(SqlConnection c, SqlTransaction t, Guid id, CancellationToken ct)
    {
        var stamp = await SourceStamp(c, t, ct).ConfigureAwait(false);
        await using var check = new SqlCommand("SELECT SourceStamp FROM dbo.BsyncCaptureGenerations WHERE Id=@id", c, t);
        check.Parameters.AddWithValue("@id", id);
        if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) as string != stamp)
            throw new InvalidOperationException("Source generation changed; abandon and restart the resnapshot.");
    }

    private static async Task<IReadOnlyList<ChangeTrackingSourceKey>> ReadGenerationPage(SqlConnection c, SqlTransaction t,
        Guid batch, int offset, int limit, CancellationToken ct)
    {
        var keys = new List<ChangeTrackingSourceKey>();
        await using var command = new SqlCommand("SELECT TOP (@limit) SourceSchema,SourceTable,SourceKey,Deleted FROM dbo.BsyncCaptureKeys WHERE Batch=@batch AND Ordinal>=@offset ORDER BY Ordinal", c, t);
        command.Parameters.AddWithValue("@limit", limit); command.Parameters.AddWithValue("@batch", batch); command.Parameters.AddWithValue("@offset", offset);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keys.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return keys.AsReadOnly();
    }

    /// <summary>
    /// Builds one durable page into host-owned rows keyed by generation ID, atomically with page progress. The callback
    /// rereads canonical data, resolves related/deleted keys and membership, and writes ONLY this isolated generation.
    /// It must not update the active scope or perform external I/O. Empty generations need no build callback.
    /// </summary>
    public async Task<ChangeTrackingGeneration> BuildGenerationPageAsync(Guid generation,
        Func<SqlConnection, SqlTransaction, Guid, IReadOnlyList<ChangeTrackingSourceKey>, CancellationToken, Task> build,
        int pageSize = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (pageSize is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        ChangeTrackingGeneration? state = null;
        try
        {
            await using var c = new SqlConnection(_connectionString); await c.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var t = c.BeginTransaction(IsolationLevel.Serializable); await Lock(c, t, cancellationToken).ConfigureAwait(false);
            state = await ReadGeneration(c, t, generation, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Missing generation.");
            if (state.Status != "building" || state.CompletedKeys == state.TotalKeys) return state;
            await CheckGenerationStamp(c, t, generation, cancellationToken).ConfigureAwait(false);
            var keys = await ReadGenerationPage(c, t, state.BatchId, state.CompletedKeys, pageSize, cancellationToken).ConfigureAwait(false);
            if (keys.Count == 0) throw new InvalidDataException("Sealed generation keys are missing.");
            await build(c, t, generation, keys, cancellationToken).ConfigureAwait(false);
            await using var progress = new SqlCommand("UPDATE dbo.BsyncCaptureGenerations SET NextOrdinal=NextOrdinal+@count,UpdatedUtc=SYSUTCDATETIME(),Failure=NULL WHERE Id=@id", c, t);
            progress.Parameters.AddWithValue("@count", keys.Count); progress.Parameters.AddWithValue("@id", generation);
            await progress.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var result = (await ReadGeneration(c, t, generation, cancellationToken).ConfigureAwait(false))!;
            await t.CommitAsync(cancellationToken).ConfigureAwait(false);
            WorkflowSchema.Record("capture_generation", "page"); return result;
        }
        catch (Exception error)
        {
            if (state is not null) await RecordGenerationFailure(state, error).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RecordGenerationFailure(ChangeTrackingGeneration state, Exception error)
    {
        try
        {
            await using var c = new SqlConnection(_connectionString); await c.OpenAsync().ConfigureAwait(false);
            await using var command = new SqlCommand("""
                UPDATE dbo.BsyncCaptureGenerations SET Failure=@failure,UpdatedUtc=SYSUTCDATETIME()
                WHERE Id=@id AND Status='building' AND Batch=@batch AND NextOrdinal=@ordinal AND UpdatedUtc=@observed
                """, c);
            command.Parameters.AddWithValue("@id", state.Id); command.Parameters.AddWithValue("@batch", state.BatchId);
            command.Parameters.AddWithValue("@ordinal", state.CompletedKeys); command.Parameters.AddWithValue("@observed", state.UpdatedUtc);
            command.Parameters.AddWithValue("@failure", error.GetType().Name.Length <= 128 ? error.GetType().Name : "ProjectionFailure");
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqlException) { /* Diagnostic failure cannot replace the original failure or advance progress. */ }
    }

    /// <summary>Seals changes since the last fully built generation boundary for subsequent bounded page builds. Retention loss refuses; no active pointer changes.</summary>
    public async Task<ChangeTrackingGeneration> CatchUpGenerationAsync(Guid generation, CancellationToken cancellationToken = default)
    {
        await using var c = new SqlConnection(_connectionString); await c.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var t = c.BeginTransaction(IsolationLevel.Snapshot); await Lock(c, t, cancellationToken).ConfigureAwait(false);
        var state = await ReadGeneration(c, t, generation, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Missing generation.");
        if (state.Status != "building" || state.CompletedKeys != state.TotalKeys) throw new InvalidOperationException("Finish building the current generation batch first.");
        await CheckGenerationStamp(c, t, generation, cancellationToken).ConfigureAwait(false);
        int maximum;
        await using (var read = new SqlCommand("SELECT MaximumKeys FROM dbo.BsyncCaptureGenerations WHERE Id=@id", c, t))
        { read.Parameters.AddWithValue("@id", generation); maximum = Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)); }
        var batch = await SealGenerationKeys(c, t, state.Boundary, maximum, cancellationToken).ConfigureAwait(false);
        await using var update = new SqlCommand("""
            UPDATE dbo.BsyncCaptureBatches SET Applied=1 WHERE Id=@previous;
            UPDATE dbo.BsyncCaptureGenerations SET Batch=@batch,Boundary=@upper,NextOrdinal=0,TotalKeys=@count,Failure=NULL,UpdatedUtc=SYSUTCDATETIME() WHERE Id=@id
            """, c, t);
        update.Parameters.AddWithValue("@previous", state.BatchId); update.Parameters.AddWithValue("@batch", batch.Batch);
        update.Parameters.AddWithValue("@upper", batch.Upper); update.Parameters.AddWithValue("@count", batch.Count); update.Parameters.AddWithValue("@id", generation);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var result = (await ReadGeneration(c, t, generation, cancellationToken).ConfigureAwait(false))!;
        await t.CommitAsync(cancellationToken).ConfigureAwait(false); return result;
    }

    /// <summary>
    /// Briefly locks contributing source tables against writers, applies the final bounded delta and activates the FULL
    /// isolated generation in one transaction with the published cursor. acquirePublicationLocks must take the same feed
    /// locks/order as synchronous routes BEFORE source locks. apply writes only generation rows; activate swaps a host
    /// pointer or replaces the complete destination scope, reconciling deletions and membership. No external I/O is allowed.
    /// If the final delta exceeds maximumKeys, catch up/build again and retry. A committed generation never activates twice.
    /// </summary>
    public async Task<bool> ActivateGenerationAsync(Guid generation,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task> acquirePublicationLocks,
        Func<SqlConnection, SqlTransaction, Guid, IReadOnlyList<ChangeTrackingSourceKey>, CancellationToken, Task> apply,
        Func<SqlConnection, SqlTransaction, Guid, CancellationToken, Task> activate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquirePublicationLocks); ArgumentNullException.ThrowIfNull(apply); ArgumentNullException.ThrowIfNull(activate);
        ChangeTrackingGeneration? state = null;
        try
        {
            await using var c = new SqlConnection(_connectionString); await c.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var t = c.BeginTransaction(IsolationLevel.Serializable); await Lock(c, t, cancellationToken).ConfigureAwait(false);
            state = await ReadGeneration(c, t, generation, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Missing generation.");
            if (state.Status == "active") return false;
            if (state.Status != "building" || state.CompletedKeys != state.TotalKeys) throw new InvalidOperationException("Generation is not completely built.");
            await acquirePublicationLocks(c, t, cancellationToken).ConfigureAwait(false);
            foreach (var table in _tables.OrderBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Table, StringComparer.Ordinal))
            {
                await using var gate = new SqlCommand($"SELECT COUNT_BIG(*) FROM {Quote(table.Schema)}.{Quote(table.Table)} WITH (TABLOCKX,HOLDLOCK)", c, t);
                await gate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            await CheckGenerationStamp(c, t, generation, cancellationToken).ConfigureAwait(false);
            var final = await SealGenerationKeys(c, t, state.Boundary, _maximumKeys, cancellationToken).ConfigureAwait(false);
            var keys = await ReadGenerationPage(c, t, final.Batch, 0, _maximumKeys, cancellationToken).ConfigureAwait(false);
            await apply(c, t, generation, keys, cancellationToken).ConfigureAwait(false);
            await activate(c, t, generation, cancellationToken).ConfigureAwait(false);
            var stamp = await SourceStamp(c, t, cancellationToken).ConfigureAwait(false);
            await using var commit = new SqlCommand("""
                UPDATE dbo.BsyncCaptureState SET Epoch=@epoch,SchemaId=@schema,Boundary=@upper,Pending=NULL,SourceStamp=@stamp WHERE Stream=@stream;
                IF @@ROWCOUNT=0 INSERT dbo.BsyncCaptureState(Stream,Epoch,SchemaId,Boundary,SourceStamp) VALUES(@stream,@epoch,@schema,@upper,@stamp);
                UPDATE dbo.BsyncCaptureBatches SET Applied=1 WHERE Id IN (@previous,@final);
                UPDATE dbo.BsyncCaptureGenerations SET Status='active',Boundary=@upper,Failure=NULL,UpdatedUtc=SYSUTCDATETIME() WHERE Id=@id;
                """, c, t);
            Identity(commit); commit.Parameters.AddWithValue("@upper", final.Upper); commit.Parameters.AddWithValue("@stamp", stamp);
            commit.Parameters.AddWithValue("@previous", state.BatchId); commit.Parameters.AddWithValue("@final", final.Batch); commit.Parameters.AddWithValue("@id", generation);
            await commit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await t.CommitAsync(cancellationToken).ConfigureAwait(false); WorkflowSchema.Record("capture_generation", "activated"); return true;
        }
        catch (Exception error) { if (state is not null) await RecordGenerationFailure(state, error).ConfigureAwait(false); throw; }
    }

    /// <summary>Explicitly abandons only the named building generation, retaining staging evidence and the old active scope/cursor. Host-owned generation rows may then be cleaned under host reader ownership.</summary>
    public async Task AbandonGenerationAsync(Guid generation, CancellationToken cancellationToken = default)
    {
        await using var c = new SqlConnection(_connectionString); await c.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var t = c.BeginTransaction(); await Lock(c, t, cancellationToken).ConfigureAwait(false);
        _ = await ReadGeneration(c, t, generation, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Missing generation.");
        await using var command = new SqlCommand("UPDATE dbo.BsyncCaptureGenerations SET Status='abandoned',UpdatedUtc=SYSUTCDATETIME() WHERE Id=@id AND Status='building'", c, t);
        command.Parameters.AddWithValue("@id", generation); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await t.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
