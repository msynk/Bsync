using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>The application's system of record: an ordinary EF Core model. Bsync never reads or writes it itself.</summary>
public sealed class TasksDb(DbContextOptions<TasksDb> options) : DbContext(options)
{
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();

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
                ChangedBy nvarchar(256) NULL,
                CONSTRAINT PK_Tasks PRIMARY KEY (Tenant, Id)
            );
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<TaskEntity>(task =>
        {
            task.ToTable("Tasks", "dbo");
            task.HasKey(t => new { t.Tenant, t.Id });
        });
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

    /// <summary>Who changed the task last, from the authenticated caller, never from the document.</summary>
    public string? ChangedBy { get; set; }
}
