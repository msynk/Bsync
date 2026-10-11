using Bsync.Server;
using Bsync.Server.AspNetCore.Blobs;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// The sample's blob rules (task F1), in its own tables: a tenant holds content it uploaded (<c>dbo.TenantBlobs</c>),
/// and a caller may read content that a live task of its tenant or its tenant's current bundle references.
/// </summary>
public sealed class TasksBlobAccess(string connectionString) : IBlobAccess
{
    public async Task<bool> HoldsAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default)
    {
        await using var db = TasksDb.Open(connectionString);
        return await db.TenantBlobs.AnyAsync(b => b.Tenant == caller.Scope && b.Sha256 == sha256, cancellationToken);
    }

    public async Task RecordAsync(SyncCallContext caller, string sha256, long size, CancellationToken cancellationToken = default)
    {
        await using var db = TasksDb.Open(connectionString);
        db.TenantBlobs.Add(new TenantBlob { Tenant = caller.Scope, Sha256 = sha256, Size = size, UploadedAt = DateTimeOffset.UtcNow });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent finish of the same content by the same tenant recorded it first.
        }
    }

    public async Task<bool> CanReadAsync(SyncCallContext caller, string sha256, CancellationToken cancellationToken = default)
    {
        await using var db = TasksDb.Open(connectionString);
        var tenant = caller.Scope;
        return await db.TaskAttachments.AnyAsync(
                a => a.Tenant == tenant && a.Sha256 == sha256 && db.Tasks.Any(t => t.Tenant == tenant && t.Id == a.TaskId && !t.Deleted),
                cancellationToken)
            || await db.BundleItems.AnyAsync(i => i.Tenant == tenant && i.Sha256 == sha256, cancellationToken);
    }
}

/// <summary>
/// Removes attachment content nothing references any more (task F1): objects no task or bundle names, older than a grace
/// period, with the tenants' upload records of that content and abandoned partial uploads. Run it on a schedule.
/// </summary>
public sealed class BlobJanitor(string connectionString, IBlobStore store, TimeProvider time)
{
    public async Task<(int Objects, int Partials, int Records)> CollectAsync(TimeSpan minimumAge, CancellationToken cancellationToken = default)
    {
        var (objects, partials) = await SyncBlobCollector.CollectAsync(store, ReferencedAsync, minimumAge, time, cancellationToken);
        var cutoff = time.GetUtcNow() - minimumAge;
        var referenced = await ReferencedAsync(cancellationToken);
        await using var db = TasksDb.Open(connectionString);
        var stale = (await db.TenantBlobs.Where(b => b.UploadedAt < cutoff).ToListAsync(cancellationToken)).Where(b => !referenced.Contains(b.Sha256)).ToList();
        db.TenantBlobs.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);
        return (objects, partials, stale.Count);
    }

    private async Task<IReadOnlySet<string>> ReferencedAsync(CancellationToken cancellationToken)
    {
        await using var db = TasksDb.Open(connectionString);
        var attachments = await db.TaskAttachments.Select(a => a.Sha256).Distinct().ToListAsync(cancellationToken);
        var bundles = await db.BundleItems.Select(i => i.Sha256).Distinct().ToListAsync(cancellationToken);
        return attachments.Concat(bundles).ToHashSet(StringComparer.Ordinal);
    }
}
