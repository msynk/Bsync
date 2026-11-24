using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Samples.Shared;
using Bsync.Server;
using Bsync.Server.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// By default the sample keeps server data in memory: it is lost when the server restarts. Replicas notice (new
// epoch), reset, and push their pending notes again. With a connection string in Bsync:PostgreSql (for example
// the environment variable Bsync__PostgreSql), data lives in PostgreSQL and several server processes can share
// it; commit hints reach clients of every process. A production server also authenticates callers (ADR-010).
ISyncAuthority<Note> authority;
if (builder.Configuration["Bsync:PostgreSql"] is { Length: > 0 } connectionString)
{
    var dataSource = Npgsql.NpgsqlDataSource.Create(connectionString);
    authority = await Bsync.Server.PostgreSql.PostgreSqlSyncAuthority<Note>.CreateAsync(new()
    {
        DataSource = dataSource,
        DocumentType = NotesJson.Default.Note,
        Collection = "notes",
    });
}
else
{
    authority = new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
    {
        Cloner = DocumentCloner.Json(NotesJson.Default.Note),
        Fingerprint = DocumentCloner.JsonFingerprint(NotesJson.Default.Note),
    });
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapSyncCollection("notes", authority, SyncJsonTypes<Note>.From(NotesJson.Default), new SyncEndpointOptions
{
    SupportedSchemas = new HashSet<string>(StringComparer.Ordinal) { NotesJson.SchemaId },

    // A demo without sign-in: every visitor shares one data set. Real apps resolve the scope from a tenant or user
    // claim and keep AllowAnonymous off.
    ResolveScope = SyncEndpointOptions.SharedScope(),
    AllowAnonymous = true,
});
app.MapFallbackToFile("index.html");

app.Run();
