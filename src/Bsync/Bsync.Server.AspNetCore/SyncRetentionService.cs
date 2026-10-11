using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bsync.Server.AspNetCore;

/// <summary>Runs <see cref="SyncRetention"/> on its interval in an ASP.NET Core host (task D5).</summary>
public sealed class SyncRetentionService(IOptions<SyncRetentionOptions> options, IEnumerable<ISyncRetentionTarget> targets, ILogger<SyncRetentionService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = new SyncRetention(options.Value, targets);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (tombstones, receipts) = await retention.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (tombstones + receipts > 0)
                {
                    logger.LogInformation("Retention purged {Tombstones} tombstone(s) and {Receipts} receipt(s).", tombstones, receipts);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A failed run changes nothing; the next one tries again.
                logger.LogWarning(error, "A retention run failed.");
            }

            try
            {
                await Task.Delay(options.Value.Interval, options.Value.TimeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>Registers <see cref="SyncRetentionService"/>.</summary>
public static class SyncRetentionServiceCollectionExtensions
{
    /// <summary>
    /// Purges tombstones and receipts older than <see cref="SyncRetentionOptions.MaxOfflineHorizon"/> (default 45 days) in
    /// every <see cref="ISyncRetentionTarget"/> registered as a service or listed in the options. Invalid options (a receipt
    /// horizon shorter than the offline horizon) fail the host at startup.
    /// </summary>
    public static IServiceCollection AddSyncRetention(this IServiceCollection services, Action<SyncRetentionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SyncRetentionOptions>()
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SyncRetentionOptions>, SyncRetentionOptionsValidator>();
        services.AddHostedService<SyncRetentionService>();
        return services;
    }

    private sealed class SyncRetentionOptionsValidator : IValidateOptions<SyncRetentionOptions>
    {
        public ValidateOptionsResult Validate(string? name, SyncRetentionOptions options) =>
            options.Validate() is [_, ..] problems ? ValidateOptionsResult.Fail(problems) : ValidateOptionsResult.Success;
    }
}
