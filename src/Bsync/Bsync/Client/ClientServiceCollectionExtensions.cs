using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bsync.Client;

/// <summary>
/// The local-replica registration recipe (docs/architecture.md#adr-007-hosting-profiles-application-api-and-lifecycle). It needs only a service container, so it
/// works in Blazor WebAssembly and Hybrid apps as well as in native UIs without Blazor (MAUI, WPF, WinForms,
/// Avalonia) and in console or service hosts.
/// </summary>
public static class ClientServiceCollectionExtensions
{
    /// <summary>
    /// Local-replica profile (Blazor WebAssembly, MAUI/WPF/WinForms/Avalonia, Blazor Hybrid, headless): one session
    /// and collection per app instance. Throws if called in an ASP.NET Core container, where a singleton replica
    /// would be shared by every user.
    /// </summary>
    /// <param name="services">The client's services.</param>
    /// <param name="options">Builds the session options (replica, transport, lease).</param>
    /// <param name="resolveAccount">Returns the signed-in account; default <c>"default"</c>.</param>
    public static IServiceCollection AddLocalSyncCollection<TDocument>(
        this IServiceCollection services,
        Func<IServiceProvider, SyncSessionOptions<TDocument>> options,
        Func<IServiceProvider, CancellationToken, Task<string>>? resolveAccount = null)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (services.Any(d => d.ServiceType.FullName == "Microsoft.AspNetCore.Hosting.IWebHostEnvironment"))
        {
            throw new InvalidOperationException(
                "A local replica is per device. Register AddServerSyncCollection on the server and AddLocalSyncCollection in the WebAssembly or native client.");
        }

        services.AddSingleton(sp =>
        {
            var built = options(sp);
            return new SyncSession<TDocument>(built.Logger is null && sp.GetService<ILoggerFactory>() is { } logging
                ? built with { Logger = logging.CreateLogger("Bsync.SyncSession") }
                : built);
        });
        services.AddSingleton<ISyncCollection<TDocument>>(sp => new LocalSyncCollection<TDocument>(
            sp.GetRequiredService<SyncSession<TDocument>>(),
            resolveAccount is null ? static _ => Task.FromResult("default") : ct => resolveAccount(sp, ct)));
        return services;
    }
}
