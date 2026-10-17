using Bsync.Protocol;
using Bsync.Samples.Notes.Client;
using Bsync.Samples.Shared;
using Bsync.Blazor.IndexedDb;
using Bsync.Transport;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped<IContentLibraryFactory, BrowserContentLibraryFactory>();

// An IndexedDB replica for this browser, replicated over HTTP by whichever tab holds the lease. The sample has
// no sign-in, so every visitor uses the default account; a real app passes resolveAccount.
var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddBrowserSyncCollection<Note>(
    "notes",
    NotesJson.Default.Note,
    (_, _) => new HttpSyncTransport<Note>(
        new HttpClient { BaseAddress = baseAddress },
        new HttpSyncTransportOptions { Collection = "notes", SchemaId = NotesJson.SchemaId },
        SyncJsonTypes<Note>.From(NotesJson.Default)),
    configure: options => options with { Interval = TimeSpan.FromSeconds(5), MaxBackoff = TimeSpan.FromSeconds(10) });

await builder.Build().RunAsync();
