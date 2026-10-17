using System.Runtime.CompilerServices;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

[assembly: InternalsVisibleTo("Bsync.Tests.SqlServer")]

namespace Bsync.Samples.Tasks.Server;

public sealed class TaskCreationConflictException() : InvalidOperationException("The task identity is already used by a different or revised creation.");

/// <summary>
/// Retryable create-and-publish recipe. The caller supplies a stable task ID; repeat after a lost commit response returns
/// the same unchanged creation. A conflicting/revised/deleted ID is refused, never recreated or republished over newer state.
/// </summary>
public sealed class TaskApiPublication(string connectionString, SqlServerSyncAuthority<TaskDocument> authority)
{
    internal Func<DbContext, IExecutionStrategy>? StrategyFactory { get; init; }
    internal Func<string, Task>? Boundary { get; init; }

    public async Task<TaskDocument> CreateAsync(string tenant, string actor, NewTask request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant); ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!SyncIds.IsValid(request.Id) || string.IsNullOrWhiteSpace(request.Title)) throw new ArgumentException("Invalid task request.", nameof(request));
        await using var strategyContext = new TasksDb(new DbContextOptionsBuilder<TasksDb>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options);
        var strategy = StrategyFactory?.Invoke(strategyContext) ?? strategyContext.Database.CreateExecutionStrategy();
        var document = await strategy.ExecuteAsync(async () =>
        {
            // Each retry owns a fresh context/connection/transaction; never reuse tracked entities after a failed attempt.
            await using var db = TasksDb.Open(connectionString);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await authority.AcquirePublicationLockAsync(tenant, transaction.GetDbTransaction(), cancellationToken);
            var existing = await db.Tasks.SingleOrDefaultAsync(t => t.Tenant == tenant && t.Id == request.Id, cancellationToken);
            if (existing is not null)
            {
                if (existing.Revision != 1 || existing.Deleted || existing.Title != request.Title || existing.ChangedBy != actor)
                    throw new TaskCreationConflictException();
                return existing.ToDocument([]);
            }
            var row = new TaskEntity { Tenant = tenant, Id = request.Id, Title = request.Title, Slug = TaskRules.Slug(request.Title), Revision = 1, ChangedBy = actor };
            db.Tasks.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            var projection = row.ToDocument([]);
            await authority.UpsertAsync(tenant, projection, transaction.GetDbTransaction(), cancellationToken);
            if (Boundary is not null) await Boundary("before-commit");
            await transaction.CommitAsync(cancellationToken);
            if (Boundary is not null) await Boundary("after-commit");
            return projection;
        });
        // Also notify after a verified replay of a committed creation. Hints may be repeated or lost without affecting correctness.
        authority.NotifyCommitted(new AuthorityCommit(tenant, [document.Id]));
        return document;
    }
}
