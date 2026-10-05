using System.Security.Claims;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Testing;
using Bsync.Tests.Conformance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bsync.Tests.Http;

/// <summary>The authority contract observed through the real HTTP endpoints and client.</summary>
public sealed class HttpAuthorityConformanceTests : AuthorityConformanceTests
{
    protected override IAuthorityConformanceDriver Driver { get; } = new HttpAuthorityDriver(new InMemoryAuthorityDriver(), TestServerHost.StartAsync);

    /// <summary>Serves one authority with the real endpoints; the caller's scope travels as an authenticated claim.</summary>
    private sealed class TestServerHost(WebApplication app) : HttpConformanceServer
    {
        public static async Task<HttpConformanceServer> StartAsync(ISyncAuthority<ConformanceDocument> authority, CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            builder.Services.AddAuthorization();
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapSyncCollection(HttpAuthorityDriver.Collection, authority, HttpAuthorityDriver.Json, new SyncEndpointOptions
            {
                SupportedSchemas = new HashSet<string>([HttpAuthorityDriver.SchemaId], StringComparer.Ordinal),
                ResolveScope = http => http.User.FindFirst("tenant")?.Value,
            }).RequireAuthorization();
            await app.StartAsync(cancellationToken);
            return new TestServerHost(app);
        }

        public override HttpClient CreateClient(SyncCallContext caller)
        {
            var client = app.GetTestServer().CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, caller.Principal.FindFirst(ClaimTypes.Name)?.Value ?? "anonymous");
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, caller.Scope);
            return client;
        }

        public override async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
