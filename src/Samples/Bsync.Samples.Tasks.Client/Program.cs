using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bsync.Blazor.IndexedDb;
using Bsync.Protocol;
using Bsync.Samples.Tasks;
using Bsync.Samples.Tasks.Client;
using Bsync.Transport;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
var session = new SignIn(baseAddress);
builder.Services.AddSingleton(session);

// One IndexedDB replica per signed-in user. The sync HttpClient is dedicated: a shared pipeline that rewrites error
// responses would hide Retry-After and the protocol's problem codes from the transport.
builder.Services.AddBrowserSyncCollection<TaskDocument>(
    TasksJson.Collection,
    TasksJson.Default.TaskDocument,
    (_, _) => new HttpSyncTransport<TaskDocument>(
        new HttpClient(new BearerHandler(session) { InnerHandler = new HttpClientHandler() }) { BaseAddress = baseAddress },
        new HttpSyncTransportOptions { Collection = TasksJson.Collection, SchemaId = TasksJson.SchemaId },
        SyncJsonTypes<TaskDocument>.From(TasksJson.Default)),
    resolveAccount: (_, _) => Task.FromResult(session.User ?? throw new InvalidOperationException("Sign in first.")),
    configure: options => options with { Interval = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(30) });

await builder.Build().RunAsync();

namespace Bsync.Samples.Tasks.Client
{
    /// <summary>Development sign-in against the sample server's token endpoint.</summary>
    public sealed class SignIn(Uri server)
    {
        public string? User { get; private set; }

        public string? Token { get; private set; }

        public async Task SignInAsync(string user, string tenant)
        {
            using var http = new HttpClient { BaseAddress = server };
            var response = await http.PostAsJsonAsync("api/token", new TokenRequest(user, tenant), TasksJson.Default.TokenRequest);
            response.EnsureSuccessStatusCode();
            Token = (await response.Content.ReadFromJsonAsync(TasksJson.Default.TokenResponse))!.Token;
            User = user;
        }
    }

    /// <summary>Adds the bearer token to sync requests.</summary>
    public sealed class BearerHandler(SignIn session) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
