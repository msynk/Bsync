using System.IO;
using System.Net.Http;
using System.Windows;
using Bsync.Client;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Samples.Shared;
using Bsync.Storage.Sqlite;
using Bsync.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Bsync.Samples.Hybrid.Wpf;

/// <summary>
/// A native host: the replica is a SQLite file under the user's local app data (or <c>--data</c>), replicated over HTTP
/// to the notes server (<c>--server</c>, default http://localhost:5000). Sync pauses while the window is minimized.
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static Options Settings { get; private set; } = new(new Uri("http://localhost:5000/"), DefaultDataDirectory(), null, "online", null);

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = Options.Parse(e.Args);
        Directory.CreateDirectory(Settings.DataDirectory);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddSingleton<IContentLibraryFactory>(new NativeContentLibraryFactory(Settings.DataDirectory));
        services.AddLocalSyncCollection<Note>(_ => new SyncSessionOptions<Note>
        {
            Host = "native",
            Cloner = DocumentCloner.Json(NotesJson.Default.Note),
            OpenReplica = async (account, cancellationToken) =>
            {
                var store = await SqliteLocalStore<Note>.OpenAsync(
                    new SqliteLocalStoreOptions { DataSource = Path.Combine(Settings.DataDirectory, $"notes-{account}.db"), Collection = "notes" },
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
        });
        Services = services.BuildServiceProvider();

        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The session is IAsyncDisposable only; the window already stopped it, so this completes at once.
        (Services as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    private static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bsync.Samples.Hybrid.Wpf");

    /// <summary>
    /// Command line: <c>--server URL --data DIR</c>, and for automated checks <c>--smoke RESULT-FILE --step online|offline|resume --text TEXT</c>.
    /// </summary>
    public sealed record Options(Uri Server, string DataDirectory, string? SmokeResult, string SmokeStep, string? SmokeText)
    {
        public static Options Parse(string[] args)
        {
            string? Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
            var server = Value("--server") is { } url ? new Uri(url.EndsWith('/') ? url : url + "/") : new Uri("http://localhost:5000/");
            return new Options(server, Value("--data") ?? DefaultDataDirectory(), Value("--smoke"), Value("--step") ?? "online", Value("--text"));
        }
    }
}
