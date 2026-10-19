using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Bsync.Samples.Tasks.Server;

/// <summary>An immutable application effect. The receiver must deduplicate IdempotencyKey, including after lost replies.</summary>
public sealed record ExternalEffect(string Scope, string CommandId, string Payload, string IdempotencyKey);
/// <summary>Receiver outcome. RetryLater retains the exact request; Rejected is a final business refusal.</summary>
public enum ExternalEffectOutcome { Succeeded, RetryLater, Rejected, ReconciliationRequired, CredentialsUnavailable }

/// <summary>
/// Optional SQL Server application-transaction outbox recipe, separate from projection publication. Dispatch runs with
/// no database transaction open. Delivery is at least once: a crash after the receiver commits repeats the stable key.
/// Completed identities/payloads are retained; never purge them without a separately enforced replay horizon.
/// </summary>
public sealed class ExternalEffectOutbox(string connectionString)
{
    /// <summary>
    /// Dispatches through explicit receiver guarantees. Claims persist the attempt before sending, so a crash in that
    /// window is conservatively ambiguous. A receiver without deduplication/status lookup is never blindly redelivered.
    /// </summary>
    public Task<int> DrainAsync(IExternalEffectReceiver receiver,
        Func<SqlConnection, SqlTransaction, ExternalEffect, ExternalEffectOutcome, CancellationToken, Task> publishResult,
        int limit = 32, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        return DrainAsync(async (effect, token) =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var command = new SqlCommand("SELECT Attempts FROM dbo.ExternalEffectOutbox WHERE EffectKey=@key", connection);
            Identify(command, effect.Scope, effect.CommandId);
            var attempts = (int)(await command.ExecuteScalarAsync(token) ?? throw new InvalidDataException("Missing claimed effect."));
            return await ExternalEffectDelivery.DispatchAsync(receiver, effect, attempts > 1, token);
        }, publishResult, limit, cancellationToken);
    }
    /// <summary>Installs the isolated example table. Production deployments use an explicit reviewed migration.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("""
            IF OBJECT_ID(N'dbo.ExternalEffectOutbox', N'U') IS NULL
            CREATE TABLE dbo.ExternalEffectOutbox (
                EffectKey char(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Scope nvarchar(256) NOT NULL, CommandId nvarchar(256) NOT NULL, Payload nvarchar(max) NOT NULL,
                State varchar(16) NOT NULL DEFAULT 'queued', Attempts int NOT NULL DEFAULT 0,
                Claim uniqueidentifier NULL, LeaseUntil datetimeoffset NULL, RetryAt datetimeoffset NULL
            );
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Enqueues with domain mutations and the queued intent result in the caller's transaction. Derive scope from the
    /// authenticated context; register/validate payloads before this call. Same identity with changed payload is refused.
    /// </summary>
    public static async Task EnqueueAsync(SqlConnection connection, SqlTransaction transaction, string scope, string commandId,
        string payload, CancellationToken ct = default)
    {
        if (!ReferenceEquals(transaction.Connection, connection)) throw new ArgumentException("Use the domain transaction's connection.", nameof(transaction));
        if (!SyncIds.IsValid(scope) || !SyncIds.IsValid(commandId)) throw new ArgumentException("Invalid scope or command identity.");
        ArgumentNullException.ThrowIfNull(payload);
        if (new UTF8Encoding(false, true).GetByteCount(payload) > 65536) throw new ArgumentException("Effect payload exceeds 64 KiB.", nameof(payload));
        await using var read = new SqlCommand("SELECT Payload FROM dbo.ExternalEffectOutbox WITH (UPDLOCK,HOLDLOCK) WHERE EffectKey=@key", connection, transaction);
        Identify(read, scope, commandId);
        if (await read.ExecuteScalarAsync(ct) is string previous)
        {
            if (previous != payload) throw new InvalidOperationException("An effect identity cannot be reused for a different payload.");
            return;
        }
        await using var insert = new SqlCommand("INSERT dbo.ExternalEffectOutbox(EffectKey,Scope,CommandId,Payload) VALUES(@key,@scope,@id,@payload)", connection, transaction);
        Identify(insert, scope, commandId); insert.Parameters.AddWithValue("@payload", payload);
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Sends a bounded batch with 30-second claims and a 20-second cooperative receiver budget. Failed attempts retry
    /// after 30 seconds; five failures quarantine the effect. Host cancellation retains the claim until expiration.
    /// publishResult runs only database work in the acknowledgement transaction and must follow global feed/domain lock
    /// ordering. Its failure rolls back acknowledgement, so the receiver key must also survive this lost-reply gap.
    /// Returns committed terminal outcomes. Concurrent/expired claims cannot acknowledge another dispatcher's work.
    /// </summary>
    public async Task<int> DrainAsync(Func<ExternalEffect, CancellationToken, Task<ExternalEffectOutcome>> send,
        Func<SqlConnection, SqlTransaction, ExternalEffect, ExternalEffectOutcome, CancellationToken, Task> publishResult,
        int limit = 32, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send); ArgumentNullException.ThrowIfNull(publishResult);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var candidates = new List<(string Scope, string Id)>();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var list = new SqlCommand("""
                SELECT TOP (@limit) Scope,CommandId FROM dbo.ExternalEffectOutbox
                WHERE State='queued' AND (RetryAt IS NULL OR RetryAt<=SYSUTCDATETIME())
                    AND (LeaseUntil IS NULL OR LeaseUntil<=SYSUTCDATETIME()) ORDER BY Scope,CommandId
                """, connection);
            list.Parameters.AddWithValue("@limit", limit);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) candidates.Add((reader.GetString(0), reader.GetString(1)));
        }
        var completed = 0;
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = Guid.NewGuid();
            string? payload;
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var take = new SqlCommand("""
                    UPDATE dbo.ExternalEffectOutbox SET Claim=@claim,LeaseUntil=DATEADD(second,30,SYSUTCDATETIME()),Attempts=Attempts+1
                    OUTPUT inserted.Payload WHERE EffectKey=@key AND State='queued'
                        AND (RetryAt IS NULL OR RetryAt<=SYSUTCDATETIME()) AND (LeaseUntil IS NULL OR LeaseUntil<=SYSUTCDATETIME())
                    """, connection);
                Identify(take, item.Scope, item.Id); take.Parameters.AddWithValue("@claim", claim);
                payload = await take.ExecuteScalarAsync(cancellationToken) as string;
            }
            if (payload is null) continue;
            // Length framing avoids scope/command separator collisions. This is an idempotency key, not a credential.
            var key = Identity(item.Scope, item.Id);
            var effect = new ExternalEffect(item.Scope, item.Id, payload, key);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                var outcome = await send(effect, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (!Enum.IsDefined(outcome)) throw new InvalidOperationException("Unknown receiver outcome.");
                if (outcome == ExternalEffectOutcome.RetryLater) { await FailAsync(effect, claim, cancellationToken); continue; }
                if (outcome == ExternalEffectOutcome.CredentialsUnavailable) { await FailAsync(effect, claim, cancellationToken, credentials: true); continue; }
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var transaction = connection.BeginTransaction();
                // Publish first to maintain feed-before-outbox lock order. A stale claim rolls this transaction back.
                await publishResult(connection, transaction, effect, outcome, cancellationToken);
                await using var acknowledge = new SqlCommand("""
                    UPDATE dbo.ExternalEffectOutbox SET State=@state,Claim=NULL,LeaseUntil=NULL,RetryAt=NULL
                    WHERE EffectKey=@key AND State='queued' AND Claim=@claim AND LeaseUntil>SYSUTCDATETIME()
                    """, connection, transaction);
                Identify(acknowledge, item.Scope, item.Id); acknowledge.Parameters.AddWithValue("@claim", claim);
                acknowledge.Parameters.AddWithValue("@state", outcome switch { ExternalEffectOutcome.Succeeded => "succeeded", ExternalEffectOutcome.Rejected => "rejected", _ => "reconciliation" });
                if (await acknowledge.ExecuteNonQueryAsync(cancellationToken) == 0) continue;
                await transaction.CommitAsync(cancellationToken);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { await FailAsync(effect, claim, cancellationToken); }
        }
        return completed;
    }

    private async Task FailAsync(ExternalEffect effect, Guid claim, CancellationToken ct, bool credentials = false)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var failed = new SqlCommand("""
            UPDATE dbo.ExternalEffectOutbox SET State=CASE WHEN @credentials=0 AND Attempts>=5 THEN 'quarantined' ELSE 'queued' END,
                Attempts=Attempts-CASE WHEN @credentials=1 THEN 1 ELSE 0 END,
                Claim=NULL,LeaseUntil=NULL,RetryAt=DATEADD(second,30,SYSUTCDATETIME())
            WHERE EffectKey=@key AND State='queued' AND Claim=@claim AND LeaseUntil>SYSUTCDATETIME()
            """, connection);
        Identify(failed, effect.Scope, effect.CommandId); failed.Parameters.AddWithValue("@claim", claim);
        failed.Parameters.AddWithValue("@credentials", credentials);
        await failed.ExecuteNonQueryAsync(ct);
    }

    private static void Identify(SqlCommand command, string scope, string id)
    {
        command.Parameters.AddWithValue("@scope", scope); command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@key", Identity(scope, id));
    }

    private static string Identity(string scope, string id) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"{scope.Length}:{scope}{id.Length}:{id}"))));
}
