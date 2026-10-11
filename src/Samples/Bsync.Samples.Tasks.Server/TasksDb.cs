using Bsync.Blobs;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>The application's system of record: an ordinary EF Core model. Bsync never reads or writes it itself.</summary>
public sealed class TasksDb(DbContextOptions<TasksDb> options) : DbContext(options)
{
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();

    /// <summary>Which content each tenant has uploaded and verified (task F1).</summary>
    public DbSet<TenantBlob> TenantBlobs => Set<TenantBlob>();

    /// <summary>Which content each task references; reading a blob requires a readable task that references it.</summary>
    public DbSet<TaskAttachment> TaskAttachments => Set<TaskAttachment>();

    /// <summary>Published bundles and their current revision (task F2).</summary>
    public DbSet<BundleEntity> Bundles => Set<BundleEntity>();

    /// <summary>The items of each bundle's current revision; reading a blob is also allowed through them.</summary>
    public DbSet<BundleItem> BundleItems => Set<BundleItem>();

    /// <summary>A context on its own connection (API endpoints).</summary>
    public static TasksDb Open(string connectionString) =>
        new(new DbContextOptionsBuilder<TasksDb>().UseSqlServer(connectionString).Options);

    /// <summary>A context that works inside the sync authority's connection and transaction (write handler).</summary>
    public static TasksDb Enlist(DbConnection connection, DbTransaction transaction)
    {
        var db = new TasksDb(new DbContextOptionsBuilder<TasksDb>().UseSqlServer(connection).Options);
        db.Database.UseTransaction(transaction);
        return db;
    }

    /// <summary>Creates the table if it is missing. A real application uses EF Core migrations.</summary>
    public static async Task EnsureTablesAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.Tasks', N'U') IS NULL
            CREATE TABLE dbo.Tasks (
                Tenant nvarchar(256) NOT NULL,
                Id nvarchar(256) NOT NULL,
                Title nvarchar(max) NOT NULL,
                Slug nvarchar(max) NOT NULL,
                Done bit NOT NULL,
                Deleted bit NOT NULL,
                Revision bigint NOT NULL,
                ChangedBy nvarchar(256) NULL,
                CONSTRAINT PK_Tasks PRIMARY KEY (Tenant, Id)
            );
            IF OBJECT_ID(N'dbo.TenantBlobs', N'U') IS NULL
            CREATE TABLE dbo.TenantBlobs (
                Tenant nvarchar(256) NOT NULL,
                Sha256 char(64) NOT NULL,
                Size bigint NOT NULL,
                UploadedAt datetimeoffset NOT NULL,
                CONSTRAINT PK_TenantBlobs PRIMARY KEY (Tenant, Sha256)
            );
            IF OBJECT_ID(N'dbo.TaskAttachments', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.TaskAttachments (
                    Tenant nvarchar(256) NOT NULL,
                    TaskId nvarchar(256) NOT NULL,
                    AttachmentId nvarchar(256) NOT NULL,
                    Sha256 char(64) NOT NULL,
                    Size bigint NOT NULL,
                    ContentType nvarchar(256) NOT NULL,
                    FileName nvarchar(512) NOT NULL,
                    CONSTRAINT PK_TaskAttachments PRIMARY KEY (Tenant, TaskId, AttachmentId)
                );
                CREATE INDEX IX_TaskAttachments_Content ON dbo.TaskAttachments (Tenant, Sha256);
            END
            IF OBJECT_ID(N'dbo.Bundles', N'U') IS NULL
            CREATE TABLE dbo.Bundles (
                Tenant nvarchar(256) NOT NULL,
                Id nvarchar(256) NOT NULL,
                Revision bigint NOT NULL,
                CONSTRAINT PK_Bundles PRIMARY KEY (Tenant, Id)
            );
            IF OBJECT_ID(N'dbo.BundleItems', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.BundleItems (
                    Tenant nvarchar(256) NOT NULL,
                    BundleId nvarchar(256) NOT NULL,
                    FileName nvarchar(512) NOT NULL,
                    Sha256 char(64) NOT NULL,
                    CONSTRAINT PK_BundleItems PRIMARY KEY (Tenant, BundleId, FileName)
                );
                CREATE INDEX IX_BundleItems_Content ON dbo.BundleItems (Tenant, Sha256);
            END
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskEntity>(task =>
        {
            task.ToTable("Tasks", "dbo");
            task.HasKey(t => new { t.Tenant, t.Id });
        });
        modelBuilder.Entity<TenantBlob>().ToTable("TenantBlobs", "dbo").HasKey(b => new { b.Tenant, b.Sha256 });
        modelBuilder.Entity<TaskAttachment>().ToTable("TaskAttachments", "dbo").HasKey(a => new { a.Tenant, a.TaskId, a.AttachmentId });
        modelBuilder.Entity<BundleEntity>().ToTable("Bundles", "dbo").HasKey(b => new { b.Tenant, b.Id });
        modelBuilder.Entity<BundleItem>().ToTable("BundleItems", "dbo").HasKey(i => new { i.Tenant, i.BundleId, i.FileName });
    }
}

/// <summary>A row of <c>dbo.Tasks</c>.</summary>
public sealed class TaskEntity
{
    public string Tenant { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public bool Done { get; set; }

    /// <summary>Soft delete: replicated deletes never remove application rows (ADR-014).</summary>
    public bool Deleted { get; set; }

    /// <summary>Incremented by every change; replicated as <see cref="TaskDocument.Revision"/>.</summary>
    public long Revision { get; set; }

    /// <summary>The replicated shape of this row and its attachments.</summary>
    public TaskDocument ToDocument(IEnumerable<TaskAttachment> attachments) => new()
    {
        Id = Id, Title = Title, Slug = Slug, Done = Done, Deleted = Deleted, Revision = Revision,
        Attachments = [.. attachments.Select(a => new BlobReference(a.AttachmentId, a.Sha256, a.Size, a.ContentType, a.FileName))],
    };

    /// <summary>Who changed the task last, from the authenticated caller, never from the document.</summary>
    public string? ChangedBy { get; set; }
}

/// <summary>Content a tenant uploaded and the server verified.</summary>
public sealed class TenantBlob
{
    public string Tenant { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>A task's attachment (the reference; the bytes are in the blob store).</summary>
public sealed class TaskAttachment
{
    public string Tenant { get; set; } = string.Empty;

    public string TaskId { get; set; } = string.Empty;

    public string AttachmentId { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
}

/// <summary>A published bundle.</summary>
public sealed class BundleEntity
{
    public string Tenant { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public long Revision { get; set; }
}

/// <summary>An item of a bundle's current revision.</summary>
public sealed class BundleItem
{
    public string Tenant { get; set; } = string.Empty;

    public string BundleId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
}
