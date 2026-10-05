using Bsync.Server.SqlServer;
using Bsync.Tests.TestSupport;
using Microsoft.Data.SqlClient;

namespace Bsync.Tests.SqlServer;

/// <summary>A fresh database on the server named by <c>BSYNC_SQLSERVER</c>, dropped on dispose.</summary>
public sealed class SqlServerDatabase : IAsyncDisposable
{
    private readonly string _admin;

    private SqlServerDatabase(string admin, string name)
    {
        _admin = admin;
        Name = name;
        ConnectionString = new SqlConnectionStringBuilder(admin) { InitialCatalog = name }.ConnectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BSYNC_SQLSERVER") is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                "Set BSYNC_SQLSERVER to a SQL Server connection string whose user may create databases, for example " +
                "\"Server=(localdb)\\MSSQLLocalDB;Integrated Security=true\" or \"Server=localhost;User Id=sa;Password=...;TrustServerCertificate=true\". " +
                "These tests create and drop their own databases.");

    /// <summary>Creates a database; with <paramref name="readCommittedSnapshot"/>, READ COMMITTED reads use row versions.</summary>
    public static async Task<SqlServerDatabase> CreateAsync(bool readCommittedSnapshot = false)
    {
        var admin = AdminConnectionString;
        var name = $"bs_test_{Guid.NewGuid():N}";
        await using var connection = new SqlConnection(admin);
        await connection.OpenAsync();
        await using (var create = new SqlCommand($"CREATE DATABASE [{name}]", connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        if (readCommittedSnapshot)
        {
            await using var rcsi = new SqlCommand($"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE", connection);
            await rcsi.ExecuteNonQueryAsync();
        }

        return new SqlServerDatabase(admin, name);
    }

    /// <summary>The connection string with its own pool, as a second server process would have.</summary>
    public string SecondProcessConnectionString => new SqlConnectionStringBuilder(ConnectionString) { ApplicationName = "bsync-second" }.ConnectionString;

    public Task<SqlServerSyncAuthority<Note>> AuthorityAsync(
        string collection = "notes",
        string? connectionString = null,
        Func<SqlServerSyncAuthorityOptions<Note>, SqlServerSyncAuthorityOptions<Note>>? configure = null)
    {
        var options = new SqlServerSyncAuthorityOptions<Note>
        {
            ConnectionString = connectionString ?? ConnectionString,
            DocumentType = NoteJsonContext.Default.Note,
            Collection = collection,
            PhysicalClock = Clocks.SystemPhysicalClock.Instance,
        };
        return SqlServerSyncAuthority<Note>.CreateAsync(configure is null ? options : configure(options));
    }

    public async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        // Only this database's pools: other test classes run in parallel.
        SqlConnection.ClearPool(new SqlConnection(ConnectionString));
        SqlConnection.ClearPool(new SqlConnection(SecondProcessConnectionString));
        await using var connection = new SqlConnection(_admin);
        await connection.OpenAsync();
        await using var drop = new SqlCommand(
            $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END",
            connection);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>One database per test class.</summary>
public sealed class SqlServerFixture : Xunit.IAsyncLifetime
{
    public SqlServerDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync() => Database = await SqlServerDatabase.CreateAsync();

    public async Task DisposeAsync() => await Database.DisposeAsync();
}
