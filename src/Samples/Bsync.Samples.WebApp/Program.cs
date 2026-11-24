using Bsync.Blazor;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Samples.Shared;
using Bsync.Samples.WebApp.Components;
using Bsync.Server;
using Bsync.Server.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// One authority serves both paths: WebAssembly replicas over HTTP, and server-rendered components in-process
// (same authorization, I18). In-memory for the sample: data is lost when the server restarts.
var authority = new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
{
    Cloner = DocumentCloner.Json(NotesJson.Default.Note),
    Fingerprint = DocumentCloner.JsonFingerprint(NotesJson.Default.Note),
});

// Server-side ISyncCollection<Note> for Interactive Server circuits, prerendering and static SSR.
builder.Services.AddServerSyncCollection<Note>(_ => authority, DocumentCloner.Json(NotesJson.Default.Note));

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapSyncCollection("notes", authority, SyncJsonTypes<Note>.From(NotesJson.Default), new SyncEndpointOptions
{
    SupportedSchemas = new HashSet<string>(StringComparer.Ordinal) { NotesJson.SchemaId },

    // A demo without sign-in: every visitor shares one data set. Real apps resolve the scope from a tenant or user
    // claim and keep AllowAnonymous off.
    ResolveScope = SyncEndpointOptions.SharedScope(),
    AllowAnonymous = true,
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Bsync.Samples.WebApp.Client._Imports).Assembly);

app.Run();
