using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Bsync.Server.SqlServer.Workflows;

/// <summary>Durable command outcome. Expired results retain immutable identities and cannot execute again.</summary>
public sealed record CommandLedgerResult(bool Replayed, bool ResultExpired, string? Result);

/// <summary>Immutable command execution within a caller-owned SQL Server transaction. Does not own a delivery queue.</summary>
public static class SqlServerCommandLedger
{
    /// <summary>Explicit deployment migration; runtime execution never creates tables.</summary>
    public static string MigrationSql { get; } = WorkflowSchema.Migration("command-ledger", """
        IF OBJECT_ID(N'dbo.BsyncCommandLedger', N'U') IS NULL
        CREATE TABLE dbo.BsyncCommandLedger (
            IdentityHash char(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            Scope nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CommandId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Actor nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PayloadHash char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Result nvarchar(max) NOT NULL, ExpiresAt datetimeoffset NOT NULL
        );
        """);

    /// <summary>
    /// Serializes retries for scope/command identity and enlists result with domain changes. Derive scope/actor from
    /// authenticated context. Acquire all authority feed locks in deterministic order BEFORE this call. Execute only
    /// database work in the callback using this same transaction; external effects belong in the effect outbox.
    /// Retry the entire caller unit of work with the same identity after deadlock, execution-strategy retry or ambiguous commit.
    /// A callback exception rolls back to a savepoint; an uncommittable transaction must be rolled back by the caller.
    /// </summary>
    public static async Task<CommandLedgerResult> ExecuteAsync(SqlConnection connection, SqlTransaction transaction,
        string scope, string commandId, string actor, string canonicalPayload, DateTimeOffset resultExpiresUtc,
        Func<CancellationToken, Task<string>> execute, CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(transaction.Connection, connection)) throw new ArgumentException("Use the caller's live transaction and connection.");
        ArgumentNullException.ThrowIfNull(execute);
        await WorkflowSchema.Validate(connection, transaction, "command-ledger", cancellationToken).ConfigureAwait(false);
        if (!SyncIds.IsValid(scope) || !SyncIds.IsValid(commandId) || !SyncIds.IsValid(actor)) throw new ArgumentException("Invalid command identity.");
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        if (Encoding.UTF8.GetByteCount(canonicalPayload) > 1048576) throw new ArgumentException("Command payload exceeds 1 MiB.");
        var identity = Digest(FormattableString.Invariant($"{scope.Length}:{scope}{commandId.Length}:{commandId}"));
        var payloadHash = Digest(canonicalPayload);
        await using var read = new SqlCommand("""
            SELECT Scope,CommandId,Actor,PayloadHash,Result,CASE WHEN ExpiresAt<=SYSUTCDATETIME() THEN 1 ELSE 0 END
            FROM dbo.BsyncCommandLedger WITH (UPDLOCK,HOLDLOCK) WHERE IdentityHash=@key
            """, connection, transaction);
        read.Parameters.Add("@key", SqlDbType.Char, 64).Value = identity;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) != scope || reader.GetString(1) != commandId || reader.GetString(2) != actor || reader.GetString(3) != payloadHash)
                    throw new InvalidOperationException("Command identity was reused with another actor or payload.");
                var expired = reader.GetInt32(5) == 1;
                return new(true, expired, expired ? null : reader.GetString(4));
            }
        }
        var savepoint = "bs_cmd_" + Guid.NewGuid().ToString("N")[..24];
        transaction.Save(savepoint);
        try
        {
            var result = await execute(cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Command result cannot be null.");
            if (Encoding.UTF8.GetByteCount(result) > 1048576) throw new InvalidDataException("Command result exceeds 1 MiB.");
            await using var insert = new SqlCommand("""
                INSERT dbo.BsyncCommandLedger(IdentityHash,Scope,CommandId,Actor,PayloadHash,Result,ExpiresAt)
                VALUES(@key,@scope,@id,@actor,@hash,@result,@expires)
                """, connection, transaction);
            insert.Parameters.AddWithValue("@key", identity); insert.Parameters.AddWithValue("@scope", scope);
            insert.Parameters.AddWithValue("@id", commandId); insert.Parameters.AddWithValue("@actor", actor);
            insert.Parameters.AddWithValue("@hash", payloadHash); insert.Parameters.AddWithValue("@result", result);
            insert.Parameters.AddWithValue("@expires", resultExpiresUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new(false, false, result);
        }
        catch
        {
            // Preserve the original error; rollback failure means the caller must abandon the transaction.
            try { transaction.Rollback(savepoint); } catch (Exception error) when (error is SqlException or InvalidOperationException) { }
            throw;
        }
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
