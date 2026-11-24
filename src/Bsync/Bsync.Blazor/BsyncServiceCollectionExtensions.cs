using System.Security.Claims;
using Bsync.Client;
using Bsync.Server;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bsync.Blazor;

/// <summary>
/// The server-connected registration recipe (docs/architecture.md#adr-007-hosting-profiles-application-api-and-lifecycle). Register exactly one
/// <see cref="ISyncCollection{TDocument}"/> per runtime: in a Blazor Web App the server project registers the
/// server-connected collection and the WebAssembly client project registers the local one
/// (<see cref="ClientServiceCollectionExtensions.AddLocalSyncCollection{TDocument}"/> in <c>Bsync</c>); each
/// runtime has its own container, so Auto render mode gets the right one on each side.
/// </summary>
public static class BsyncServiceCollectionExtensions
{
    /// <summary>
    /// Server-connected profile (Interactive Server, prerendering, static SSR): a scoped collection per circuit
    /// or request that calls <paramref name="authority"/> in-process as the authenticated user.
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <param name="authority">Resolves the authority (it must also implement <see cref="ISyncDocumentReader{TDocument}"/>).</param>
    /// <param name="cloner">Deep-clone function for documents.</param>
    /// <param name="resolveScope">
    /// Derives the scope from the authenticated user; return <see langword="null"/> to deny. Default: <c>"default"</c>.
    /// </param>
    public static IServiceCollection AddServerSyncCollection<TDocument>(
        this IServiceCollection services,
        Func<IServiceProvider, ISyncAuthority<TDocument>> authority,
        Func<TDocument, TDocument> cloner,
        Func<ClaimsPrincipal, string?>? resolveScope = null)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(cloner);
        resolveScope ??= static _ => "default";

        services.TryAddSingleton(_ => new ServerSyncClock());
        services.AddScoped<ISyncCollection<TDocument>>(sp =>
        {
            var authentication = sp.GetService<AuthenticationStateProvider>();
            return new ServerSyncCollection<TDocument>(
                authority(sp),
                async _ =>
                {
                    var user = authentication is null
                        ? new ClaimsPrincipal(new ClaimsIdentity())
                        : (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
                    var scope = resolveScope(user)
                        ?? throw new SyncTransportException(SyncErrorCodes.Forbidden, "No scope is available for the current user.", isTransient: false);
                    return new SyncCallContext(user, scope);
                },
                sp.GetRequiredService<ServerSyncClock>().Clock,
                cloner);
        });
        return services;
    }
}
