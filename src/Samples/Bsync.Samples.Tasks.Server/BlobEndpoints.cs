using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// The blob routes (task F1). Uploads are resumable and per tenant: a tenant proves it has the content by uploading it,
/// even when another tenant already stored the same bytes, so a hash never reveals what others hold; the object itself
/// is stored once. A caller may read a blob only if a task it can read, or its team's current bundle, references it.
/// </summary>
public static class BlobEndpoints
{
    /// <summary>The largest blob the sample accepts.</summary>
    public const long MaxSize = 200L * 1024 * 1024;

    public static void Map(WebApplication app, string connectionString, IBlobStore store)
    {
        var blobs = app.MapGroup("/api/blobs/{sha256}").RequireAuthorization();

        // Starts or resumes an upload: how much the server already holds.
        blobs.MapPost("/uploads", async (string sha256, long size, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (Tenant(user) is not { } tenant || !IsHash(sha256) || size < 0)
            {
                return Results.BadRequest();
            }

            if (size > MaxSize)
            {
                return Results.Problem(statusCode: 413, title: BlobCodes.TooLarge);
            }

            await using var db = TasksDb.Open(connectionString);
            return await db.TenantBlobs.AnyAsync(b => b.Tenant == tenant && b.Sha256 == sha256, cancellationToken)
                ? Results.Ok(new BlobUploadStatus(size, Complete: true))
                : Results.Ok(new BlobUploadStatus(await store.GetReceivedAsync(FileSystemBlobStore.UploadKey(tenant, sha256), cancellationToken), Complete: false));
        });

        // Appends one chunk at the offset the client believes the server is at; 409 with the real offset otherwise.
        blobs.MapPut("/uploads/{offset:long}", async (string sha256, long offset, HttpRequest request, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (Tenant(user) is not { } tenant || !IsHash(sha256) || offset < 0)
            {
                return Results.BadRequest();
            }

            var upload = FileSystemBlobStore.UploadKey(tenant, sha256);
            try
            {
                return await store.AppendAsync(upload, offset, request.Body, MaxSize, cancellationToken) is { } received
                    ? Results.Ok(new BlobUploadStatus(received, Complete: false))
                    : Results.Conflict(new BlobUploadStatus(await store.GetReceivedAsync(upload, cancellationToken), Complete: false));
            }
            catch (InvalidDataException)
            {
                return Results.Problem(statusCode: 413, title: BlobCodes.TooLarge);
            }
        });

        // Verifies the hash and records that this tenant holds the content. Repeating it (a lost response) is harmless.
        blobs.MapPost("/uploads/finish", async (string sha256, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (Tenant(user) is not { } tenant || !IsHash(sha256))
            {
                return Results.BadRequest();
            }

            await using var db = TasksDb.Open(connectionString);
            var owned = await db.TenantBlobs.SingleOrDefaultAsync(b => b.Tenant == tenant && b.Sha256 == sha256, cancellationToken);
            if (owned is not null)
            {
                return Results.Ok(new BlobUploadStatus(owned.Size, Complete: true));
            }

            var upload = FileSystemBlobStore.UploadKey(tenant, sha256);
            var size = await store.GetReceivedAsync(upload, cancellationToken);
            if (!await store.CompleteAsync(upload, sha256, cancellationToken))
            {
                return Results.Problem(statusCode: 422, title: BlobCodes.HashMismatch);
            }

            db.TenantBlobs.Add(new TenantBlob { Tenant = tenant, Sha256 = sha256, Size = size });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent finish of the same content by the same tenant recorded it first.
            }

            return Results.Ok(new BlobUploadStatus(size, Complete: true));
        });

        // Reads a blob, with range requests (resumable downloads), if a readable task of the caller references it.
        blobs.MapGet("/", async (string sha256, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            if (Tenant(user) is not { } tenant || !IsHash(sha256))
            {
                return Results.BadRequest();
            }

            await using var db = TasksDb.Open(connectionString);
            var readable =
                await db.TaskAttachments.AnyAsync(
                    a => a.Tenant == tenant && a.Sha256 == sha256 && db.Tasks.Any(t => t.Tenant == tenant && t.Id == a.TaskId && !t.Deleted),
                    cancellationToken)
                || await db.BundleItems.AnyAsync(i => i.Tenant == tenant && i.Sha256 == sha256, cancellationToken);
            if (!readable)
            {
                return Results.NotFound(); // not "forbidden": whether content exists is not disclosed
            }

            // An object store serves the bytes itself through a presigned URL valid for a few minutes (range requests and
            // resumption work there too); otherwise the route streams them.
            if (await store.PresignReadAsync(sha256, TimeSpan.FromMinutes(5), cancellationToken) is { } direct)
            {
                return Results.Redirect(direct.ToString(), permanent: false, preserveMethod: true);
            }

            return await store.OpenReadAsync(sha256, cancellationToken) is { } stream
                ? Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true)
                : Results.NotFound();
        });
    }

    private static string? Tenant(ClaimsPrincipal user) => user.FindFirst("tenant")?.Value;

    private static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
}
