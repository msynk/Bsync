using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bsync.Server.AspNetCore;

/// <summary>Host integration for bounded durable workflow batches. This schedules callbacks, not a second durable queue.</summary>
public static class BsyncWorkflowWorkerExtensions
{
    /// <summary>
    /// Registers one serial worker with a fresh asynchronous DI scope per batch. The callback uses existing durable
    /// outbox/capture APIs and must honor cancellation cooperatively. Shutdown cancels admission and joins the active
    /// batch before disposing its scope, even if the host's stop wait is canceled. Configure the host shutdown budget
    /// accordingly; a noncooperative external operation cannot be forcibly recalled. No migrations run here.
    /// </summary>
    public static IServiceCollection AddBsyncWorkflowWorker(this IServiceCollection services, string name,
        Func<IServiceProvider, CancellationToken, Task> runBatch, TimeSpan interval, TimeSpan? failureBackoff = null)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(runBatch);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128) throw new ArgumentException("Use a stable non-secret worker name of 1..128 characters.", nameof(name));
        var backoff = failureBackoff ?? TimeSpan.FromSeconds(30);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        if (backoff <= TimeSpan.Zero || backoff > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(failureBackoff));
        if (services.Any(d => d.ImplementationInstance is WorkerRegistration r && r.Name == name)) throw new ArgumentException("Worker name is already registered.", nameof(name));
        services.AddSingleton(new WorkerRegistration(name));
        services.AddSingleton<IHostedService>(sp => new WorkflowWorker(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Bsync.Workflows"), name, runBatch, interval, backoff));
        return services;
    }
    private sealed record WorkerRegistration(string Name);
    private sealed class WorkflowWorker(IServiceScopeFactory scopes, ILogger logger, string name,
        Func<IServiceProvider, CancellationToken, Task> runBatch, TimeSpan interval, TimeSpan backoff) : BackgroundService
    {
        private static readonly Meter Meter = new("Bsync.WorkflowWorkers");
        private static readonly Counter<long> Batches = Meter.CreateCounter<long>("bsync.worker.batches");
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = interval;
                try
                {
                    await using (var scope = scopes.CreateAsyncScope())
                        await runBatch(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
                    Batches.Add(1, new("worker", name), new("outcome", "completed"));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Batches.Add(1, new("worker", name), new("outcome", "failed"));
                    // Payloads, credentials and signed URLs can occur in arbitrary callback exception messages.
                    logger.LogWarning("Workflow worker {Worker} failed with {ErrorType}; durable work remains owned by its store.", name, error.GetType().Name);
                    delay = backoff;
                }
                try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(CancellationToken.None);
    }
}
