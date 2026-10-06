using Bsync.Client;
using Bsync.Maui;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Samples.Shared;
using Bsync.Storage.Sqlite;
using Bsync.Transport;

namespace Bsync.Samples.Hybrid.Maui;

public static class MauiProgram
{
    /// <summary>Command line: <c>--server URL --data DIR</c>, and for automated checks <c>--smoke RESULT-FILE --text TEXT</c>.</summary>
    public static (Uri Server, string Data, string? Smoke, string? Text) Settings { get; private set; }

    public static MauiApp CreateMauiApp()
    {
        var args = Environment.GetCommandLineArgs();
        string? Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var server = Value("--server") is { } url ? new Uri(url.EndsWith('/') ? url : url + "/") : new Uri("http://localhost:5000/");
        Settings = (server, Value("--data") ?? Path.Combine(FileSystem.AppDataDirectory, "replica"), Value("--smoke"), Value("--text"));
        Directory.CreateDirectory(Settings.Data);

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        // The same recipe as the WPF sample: a SQLite replica per account, replicated over HTTP by one session.
        builder.Services.AddLocalSyncCollection<Note>(_ => new SyncSessionOptions<Note>
        {
            Host = "native",
            Cloner = DocumentCloner.Json(NotesJson.Default.Note),
            OpenReplica = async (account, cancellationToken) =>
            {
                var store = await SqliteLocalStore<Note>.OpenAsync(
                    new SqliteLocalStoreOptions { DataSource = Path.Combine(Settings.Data, $"notes-{account}.db"), Collection = "notes" },
                    NotesJson.Default.Note,
                    cancellationToken);
                var identity = await store.GetReplicaIdentityAsync(cancellationToken);
                return new LocalReplica<Note>(store, identity.Incarnation);
            },
            CreateTransport = _ => new HttpSyncTransport<Note>(
                new HttpClient { BaseAddress = Settings.Server },
                new HttpSyncTransportOptions { Collection = "notes", SchemaId = NotesJson.SchemaId },
                SyncJsonTypes<Note>.From(NotesJson.Default)),
            LiveHints = true,
            Interval = TimeSpan.FromSeconds(10),
            MaxBackoff = TimeSpan.FromSeconds(10),

            // Native lifecycle (ADR-007, task G1): sync when the network returns; no replication while the app is in the
            // background; resuming syncs at once.
        }.UseMauiLifecycle());

        return builder.Build();
    }
}
