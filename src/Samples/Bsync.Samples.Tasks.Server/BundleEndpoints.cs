using System.Security.Claims;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Publishing bundles (task F2): an ordinary API endpoint records the new revision and its items in the application's
/// tables and publishes the manifest in the same transaction. Replicas only read bundles.
/// </summary>
public static class BundleEndpoints
{
    public static void Map(WebApplication app, string connectionString, SqlServerSyncAuthority<BundleManifest> bundles)
    {
        app.MapPost("/api/bundles", async (PublishBundle request, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var tenant = user.FindFirst("tenant")?.Value;
            if (tenant is null || !SyncIds.IsValid(request.Id) || request.Items.Count == 0
                || request.Items.Select(i => i.FileName).Distinct(StringComparer.Ordinal).Count() != request.Items.Count)
            {
                return Results.BadRequest();
            }

            await using var db = TasksDb.Open(connectionString);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Every item must already be uploaded and verified by this tenant: a manifest never names missing bytes.
            var hashes = request.Items.Select(i => i.Sha256).Distinct(StringComparer.Ordinal).ToList();
            var held = await db.TenantBlobs.Where(b => b.Tenant == tenant && hashes.Contains(b.Sha256)).ToDictionaryAsync(b => b.Sha256, b => b.Size, cancellationToken);
            if (request.Items.Any(i => !held.TryGetValue(i.Sha256, out var size) || size != i.Size))
            {
                return Results.Problem(statusCode: 409, title: "bundle-content-missing");
            }

            var bundle = await db.Bundles.SingleOrDefaultAsync(b => b.Tenant == tenant && b.Id == request.Id, cancellationToken);
            if (bundle is null)
            {
                bundle = new BundleEntity { Tenant = tenant, Id = request.Id };
                db.Bundles.Add(bundle);
            }

            bundle.Revision++;
            db.BundleItems.RemoveRange(await db.BundleItems.Where(i => i.Tenant == tenant && i.BundleId == request.Id).ToListAsync(cancellationToken));
            db.BundleItems.AddRange(request.Items.Select(i => new BundleItem { Tenant = tenant, BundleId = request.Id, FileName = i.FileName, Sha256 = i.Sha256 }));
            await db.SaveChangesAsync(cancellationToken);

            var manifest = new BundleManifest { Id = request.Id, Revision = bundle.Revision, Items = [.. request.Items] };
            await bundles.UpsertAsync(tenant, manifest, transaction.GetDbTransaction(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            bundles.NotifyCommitted(new AuthorityCommit(tenant, [manifest.Id]));
            return Results.Ok(manifest);
        }).RequireAuthorization();
    }
}

/// <summary>Replicas read bundles; only the server publishes them.</summary>
public sealed class ReadOnlyWriteHandler<TDocument> : ISyncWriteHandler<TDocument>
    where TDocument : class, ISyncEntity
{
    public ValueTask<SyncWriteDecision<TDocument>> HandleAsync(SyncWriteContext<TDocument> write, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SyncWriteDecision<TDocument>.Reject("read-only", "This collection is published by the server."));
}
