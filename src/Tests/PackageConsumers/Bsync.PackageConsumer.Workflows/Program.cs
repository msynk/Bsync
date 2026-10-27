using Bsync.Server.SqlServer.Workflows;
using Bsync.Server.AspNetCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Package-only, neutral workflow example. Creates and drops its own random database; never alters an application DB.
var admin = Environment.GetEnvironmentVariable("BSYNC_SQLSERVER") ?? throw new InvalidOperationException("Set BSYNC_SQLSERVER to a test server where databases may be created.");
var database = "bs_capabilities_" + Guid.NewGuid().ToString("N");
await using var management = new SqlConnection(admin);
await management.OpenAsync();
await new SqlCommand($"CREATE DATABASE [{database}]", management).ExecuteNonQueryAsync();
var configured = new SqlConnectionStringBuilder(admin) { InitialCatalog = database }.ConnectionString;
try
{
    await using var connection = new SqlConnection(configured);
    await connection.OpenAsync();
    async Task Sql(string sql) => await new SqlCommand(sql, connection).ExecuteNonQueryAsync();
    await Sql($"ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON; ALTER DATABASE [{database}] SET CHANGE_TRACKING=ON (CHANGE_RETENTION=2 DAYS,AUTO_CLEANUP=ON);");
    await Sql(SqlServerCommandLedger.MigrationSql);
    await Sql(SqlServerProjectionOutbox.MigrationSql);
    await Sql(SqlServerChangeTrackingCapture.MigrationSql);
    await new ExternalEffectOutbox(configured).InitializeAsync();
    await Sql("CREATE TABLE dbo.Notes(Id int NOT NULL PRIMARY KEY, Body nvarchar(256) NOT NULL); ALTER TABLE dbo.Notes ENABLE CHANGE_TRACKING; CREATE TABLE dbo.ReadModel(Id int NOT NULL PRIMARY KEY, Body nvarchar(256) NOT NULL);");
    var projections = new SqlServerProjectionOutbox(configured, "notes-v1");
    await using (var transaction = connection.BeginTransaction())
    {
        var result = await SqlServerCommandLedger.ExecuteAsync(connection, transaction, "team", "create-note", "authorized-actor", "{\"id\":1}", DateTimeOffset.UtcNow.AddDays(1), async ct =>
        {
            await new SqlCommand("INSERT dbo.Notes VALUES(1,N'hello')", connection, transaction).ExecuteNonQueryAsync(ct);
            await projections.RequestAsync(connection, transaction, "team", "1", ct);
            return "created";
        });
        if (result.Replayed) throw new InvalidOperationException("Unexpected replay.");
        await transaction.CommitAsync();
    }
    await using (var transaction = connection.BeginTransaction())
    {
        var replay = await SqlServerCommandLedger.ExecuteAsync(connection, transaction, "team", "create-note", "authorized-actor", "{\"id\":1}", DateTimeOffset.UtcNow.AddDays(1), _ => throw new InvalidOperationException("Replay executed domain code."));
        if (!replay.Replayed || replay.Result != "created") throw new InvalidOperationException("Lost durable result.");
        await transaction.CommitAsync();
    }
    // This neutral read model has no Bsync feed. A feed publisher calls AcquirePublicationLockAsync here, first.
    await projections.DrainAsync((_, _, _, _) => Task.CompletedTask, async (c, tx, _, _, ct) =>
        await new SqlCommand("DELETE dbo.ReadModel; INSERT dbo.ReadModel SELECT * FROM dbo.Notes", c, tx).ExecuteNonQueryAsync(ct));
    var capture = new SqlServerChangeTrackingCapture(configured, "notes", "external-restore-epoch-1", "projection-v1", [new("dbo", "Notes", "Id")]);
    await capture.StageAsync();
    await capture.PublishAsync(async (c, tx, batch, ct) =>
    {
        // Complete generation replacement reconciles deletions. Real hosts derive membership and call ReplaceScopeAsync.
        await new SqlCommand("DELETE dbo.ReadModel; INSERT dbo.ReadModel SELECT * FROM dbo.Notes", c, tx).ExecuteNonQueryAsync(ct);
    });
    if ((int)(await new SqlCommand("SELECT COUNT(*) FROM dbo.ReadModel", connection).ExecuteScalarAsync())! != 1) throw new InvalidOperationException("Publication missing.");
    // Large resnapshots keep host-owned rows isolated by generation until one complete-scope activation.
    await Sql("CREATE TABLE dbo.GenerationNotes(Generation uniqueidentifier NOT NULL, Id int NOT NULL, Body nvarchar(256) NOT NULL, PRIMARY KEY(Generation,Id))");
    async Task BuildGeneration(SqlConnection c, SqlTransaction tx, Guid id, IReadOnlyList<ChangeTrackingSourceKey> keys, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            await using var command = new SqlCommand("DELETE dbo.GenerationNotes WHERE Generation=@id AND Id=@key; INSERT dbo.GenerationNotes SELECT @id,Id,Body FROM dbo.Notes WHERE Id=@key", c, tx);
            command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@key", int.Parse(key.Key));
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    var generation = await capture.BeginGenerationAsync("external-restore-epoch-1");
    while (generation.CompletedKeys < generation.TotalKeys) generation = await capture.BuildGenerationPageAsync(generation.Id, BuildGeneration);
    await Sql("UPDATE dbo.Notes SET Body=N'changed during generation build'");
    await capture.ActivateGenerationAsync(generation.Id, (_, _, _) => Task.CompletedTask, BuildGeneration, async (c, tx, id, ct) =>
    {
        await using var command = new SqlCommand("DELETE dbo.ReadModel; INSERT dbo.ReadModel SELECT Id,Body FROM dbo.GenerationNotes WHERE Generation=@id", c, tx);
        command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync(ct);
    });
    if ((string)(await new SqlCommand("SELECT Body FROM dbo.ReadModel WHERE Id=1", connection).ExecuteScalarAsync())! != "changed during generation build")
        throw new InvalidOperationException("Generation handoff missed a concurrent source change.");
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton(capture);
    builder.Services.AddBsyncWorkflowWorker("capture-health", async (services, ct) =>
    {
        var health = await services.GetRequiredService<SqlServerChangeTrackingCapture>().GetHealthAsync(ct);
        if (health.PublishedVersion is null || health.PendingBatchId is not null || health.RequiresResnapshot)
            throw new InvalidOperationException("Unexpected capture health.");
        observed.TrySetResult();
    }, TimeSpan.FromHours(1));
    using var host = builder.Build(); await host.StartAsync();
    await observed.Task.WaitAsync(TimeSpan.FromSeconds(30)); await host.StopAsync();
    Console.WriteLine("Package-only command replay, projection, capture, health and joined DI worker passed.");
}
finally
{
    SqlConnection.ClearAllPools();
    await new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]", management).ExecuteNonQueryAsync();
}
