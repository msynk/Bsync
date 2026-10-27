using System.Diagnostics.Metrics;
using Microsoft.Data.SqlClient;

namespace Bsync.Server.SqlServer.Workflows;

internal static class WorkflowSchema
{
    private static readonly Meter Meter = new("Bsync.RelationalWorkflows");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("bsync.relational.operations", "{operation}", "Committed worker outcomes; command execution counts are caller-transaction attempts, not commits.");
    internal static void Record(string operation, string outcome) => Operations.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("outcome", outcome));

    // Names below are compile-time library identifiers, never application input.
    internal static string Migration(string name, string body, int version = 1) => $$"""
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        IF OBJECT_ID(N'dbo.BsyncWorkflowSchema', N'U') IS NULL
            CREATE TABLE dbo.BsyncWorkflowSchema(Name varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY, Version int NOT NULL);
        IF EXISTS(SELECT 1 FROM dbo.BsyncWorkflowSchema WHERE Name='{{name}}' AND (Version<1 OR Version>{{version}}))
            THROW 51000, 'Unsupported Bsync workflow schema; retained.', 1;
        {{body}}
        IF NOT EXISTS(SELECT 1 FROM dbo.BsyncWorkflowSchema WHERE Name='{{name}}') INSERT dbo.BsyncWorkflowSchema VALUES('{{name}}',{{version}});
        ELSE UPDATE dbo.BsyncWorkflowSchema SET Version={{version}} WHERE Name='{{name}}';
        COMMIT TRANSACTION;
        """;

    internal static async Task Validate(SqlConnection connection, SqlTransaction? transaction, string name, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT Version FROM dbo.BsyncWorkflowSchema WHERE Name=@name", connection, transaction);
        command.Parameters.AddWithValue("@name", name);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not int version || version != (name == "change-tracking" ? 2 : 1))
            throw new NotSupportedException("Missing or unsupported Bsync workflow schema. Run the explicit matching migration; existing data is retained.");
    }
}
