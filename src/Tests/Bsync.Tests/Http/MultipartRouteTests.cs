using System.Net;
using Bsync.Blobs;
using Bsync.Server.AspNetCore.Blobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bsync.Tests.Http;

public sealed class MultipartRouteTests
{
    [Fact]
    public async Task AuthenticatedControlProtocolUsesCurrentScopeAndExplicitUnsupportedNegotiation()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        var provider = new Provider(); var calls = 0;
        app.MapSyncMultipartUploads((http, _) =>
        {
            calls++; Assert.Equal("alice", http.User.Identity!.Name);
            return Task.FromResult<IMultipartUploadProtocol?>(http.User.FindFirst("tenant")?.Value == "supported" ? provider : null);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        var protocol = new HttpMultipartUploadProtocol(client, new("http://localhost/sync/multipart/"));
        var id = Guid.NewGuid().ToString("N"); var hash = new string('a', 64);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<HttpRequestException>(() => protocol.StartAsync(id, hash, 7, default))).StatusCode);
        Assert.Equal(0, calls);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "alice");
        Assert.Null(await protocol.StartAsync(id, hash, 7, default));
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, "supported");
        Assert.Equal(7, (await protocol.StartAsync(id, hash, 7, default))!.Size);
        var ticket = await protocol.AuthorizePartAsync(id, hash, 1, default); Assert.Equal(7, ticket.Length);
        Assert.True((await protocol.CompleteAsync(id, hash, default)).VerifiedComplete);
        await protocol.AbortAsync(id, hash, default); Assert.True(provider.Aborted);
        using var response = await client.PostAsync($"sync/multipart/{hash}/start/{id}?size=7", null);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(6, calls);
    }
    private sealed class Provider : IMultipartUploadProtocol
    {
        public Uri Endpoint => new("https://authority.test/multipart/");
        public bool Aborted;
        public Task<MultipartUploadSession?> StartAsync(string operationId, string sha256, long size, CancellationToken cancellationToken) => Task.FromResult<MultipartUploadSession?>(new(1, operationId, sha256, size, 5242880, [], false));
        public Task<MultipartPartTicket> AuthorizePartAsync(string sessionId, string sha256, int number, CancellationToken cancellationToken) => Task.FromResult(new MultipartPartTicket(new("https://object.test/part?signature=secret"), number, 0, 7));
        public Task<MultipartUploadSession> CompleteAsync(string sessionId, string sha256, CancellationToken cancellationToken) => Task.FromResult(new MultipartUploadSession(1, sessionId, sha256, 7, 5242880, [], true));
        public Task AbortAsync(string sessionId, string sha256, CancellationToken cancellationToken) { Aborted = true; return Task.CompletedTask; }
    }
}
