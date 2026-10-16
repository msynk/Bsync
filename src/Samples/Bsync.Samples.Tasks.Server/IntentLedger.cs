using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Server;

namespace Bsync.Samples.Tasks.Server;

// Application-owned command ledger. The compact identity/hash row is never purged with transport receipts or result
// documents. Expired results may be cleared, but the tombstone must remain to refuse re-execution indefinitely.
internal static class IntentLedger
{
    internal static string Fingerprint(TaskIntent intent, string? actor) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new IntentRequest(intent.RequestSchema, intent.Kind, intent.TaskId, intent.TaskRevision, intent.Title, intent.CreatedAt, actor), IntentLedgerJson.Default.IntentRequest))));

    internal static async Task<(bool Found, string Hash, string? Result)> ReadAsync(SyncWriteContext<TaskIntent> write, CancellationToken ct)
    {
        await using var command = Command(write, """
            SELECT RequestHash, CASE WHEN ResultExpiresAt > SYSUTCDATETIME() THEN Result ELSE NULL END
            FROM dbo.IntentExecutions WITH (UPDLOCK,HOLDLOCK)
            WHERE Tenant=@tenant AND CommandId=@id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (false, "", null);
        return (true, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
    internal static async Task SaveAsync(SyncWriteContext<TaskIntent> write, string hash, CancellationToken ct)
    {
        await using var command = Command(write, """
            INSERT INTO dbo.IntentExecutions(Tenant,CommandId,RequestHash,Result,ResultExpiresAt)
            VALUES(@tenant,@id,@hash,@result,DATEADD(day,90,SYSUTCDATETIME()))
            """);
        Parameter(command, "@hash", hash);
        Parameter(command, "@result", JsonSerializer.Serialize(write.Submitted, TasksJson.Default.TaskIntent));
        await command.ExecuteNonQueryAsync(ct);
    }
    private static DbCommand Command(SyncWriteContext<TaskIntent> write, string sql)
    {
        var command = write.Connection!.CreateCommand(); command.Transaction = write.Transaction; command.CommandText = sql;
        Parameter(command, "@tenant", write.Caller.Scope); Parameter(command, "@id", write.Submitted.Id);
        return command;
    }
    private static void Parameter(DbCommand command, string name, string value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
}

internal sealed record IntentRequest(int Schema, string Kind, string Target, long? Revision, string? Title, DateTimeOffset CreatedAt, string? Actor);
[JsonSerializable(typeof(IntentRequest))]
internal partial class IntentLedgerJson : JsonSerializerContext;
