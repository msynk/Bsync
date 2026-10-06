using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bsync.Tests.Http;

/// <summary>An in-memory ASP.NET Core server exposing one collection through the real endpoints.</summary>
public sealed class SyncTestHost : IAsyncDisposable
{
    public const string SchemaId = "notes-v1";

    private readonly WebApplication _app;

    private SyncTestHost(WebApplication app) => _app = app;

    public static SyncJsonTypes<Note> Json { get; } = SyncJsonTypes<Note>.From(NoteJsonContext.Default);

    /// <summary>Starts a host. With <paramref name="requireAuthentication"/>, the scope is the caller's tenant claim.</summary>
    public static async Task<SyncTestHost> StartAsync(
        ISyncAuthority<Note> authority,
        bool requireAuthentication = false,
        long maxRequestBodyBytes = 4 * 1024 * 1024,
        IReadOnlyCollection<string>? supportedSchemas = null,
        ILoggerProvider? logs = null,
        ISyncReplicaAudit? replicaAudit = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        var group = app.MapSyncCollection("notes", authority, Json, new SyncEndpointOptions
        {
            SupportedSchemas = new HashSet<string>(supportedSchemas ?? [SchemaId], StringComparer.Ordinal),
            ResolveScope = requireAuthentication ? http => http.User.FindFirst("tenant")?.Value : static _ => "default",
            MaxRequestBodyBytes = maxRequestBodyBytes,
            ReplicaAudit = replicaAudit,
        });
        if (requireAuthentication)
        {
            group.RequireAuthorization();
        }

        await app.StartAsync();
        return new SyncTestHost(app);
    }

    /// <summary>An HTTP client authenticated as <paramref name="user"/> in <paramref name="tenant"/> (anonymous when null).</summary>
    public HttpClient Client(string? user = null, string? tenant = null)
    {
        var client = _app.GetTestServer().CreateClient();
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        }

        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenant);
        }

        return client;
    }

    public HttpSyncTransport<Note> Transport(string? user = null, string? tenant = null, TimeSpan? timeout = null, string schemaId = SchemaId) =>
        new(Client(user, tenant), new HttpSyncTransportOptions { Collection = "notes", SchemaId = schemaId, RequestTimeout = timeout ?? TimeSpan.FromSeconds(30) }, Json);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
