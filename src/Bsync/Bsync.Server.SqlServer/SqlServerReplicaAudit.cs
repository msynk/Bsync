using System.Data;
using Microsoft.Data.SqlClient;

namespace Bsync.Server.SqlServer;

/// <summary>
/// An <see cref="ISyncReplicaAudit"/> in a table of the application's SQL Server database (task H): one row each time a
/// replica reaches a new checkpoint of a feed (a repeated checkpoint is not recorded again). The table
/// <c>[schema].[replica_acknowledgements]</c> is created on first use; it is not part of the authority's schema and can be
/// purged by age like any log table.
/// </summary>
public sealed class SqlServerReplicaAudit : ISyncReplicaAudit
{
    private readonly string _connectionString;
    private readonly string _table;
    private readonly SemaphoreSlim _created = new(1, 1);
    private volatile bool _exists;

    /// <param name="connectionString">The application's database.</param>
    /// <param name="schema">The database schema of the table. Default <c>bsync</c> (shared with the authority).</param>
    public SqlServerReplicaAudit(string connectionString, string schema = "bsync")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (!schema.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException("The schema name may contain only ASCII letters, digits and underscores.", nameof(schema));
        }

        _connectionString = connectionString;
        _table = $"[{schema}].[replica_acknowledgements]";
        Schema = schema;
    }

    /// <summary>The database schema of the table.</summary>
    public string Schema { get; }

    /// <inheritdoc />
    public async Task RecordAsync(ReplicaAcknowledgement acknowledgement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            $"""
            INSERT INTO {_table} (replica, account, collection, scope, [checkpoint], acknowledged_at)
            SELECT @replica, @account, @collection, @scope, @checkpoint, @at
            WHERE NOT EXISTS (
                SELECT 1 FROM (
                    SELECT TOP (1) [checkpoint] FROM {_table} WITH (UPDLOCK, HOLDLOCK)
                    WHERE replica = @replica AND collection = @collection AND scope = @scope
                    ORDER BY id DESC) AS latest
                WHERE latest.[checkpoint] = @checkpoint)
            """,
            connection);
        command.Parameters.Add("@replica", SqlDbType.NVarChar, 256).Value = acknowledgement.Replica;
        command.Parameters.Add("@account", SqlDbType.NVarChar, 256).Value = (object?)acknowledgement.Account ?? DBNull.Value;
        command.Parameters.Add("@collection", SqlDbType.NVarChar, 256).Value = acknowledgement.Collection;
        command.Parameters.Add("@scope", SqlDbType.NVarChar, 256).Value = acknowledgement.Scope;
        command.Parameters.Add("@checkpoint", SqlDbType.NVarChar, 1024).Value = acknowledgement.Checkpoint;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = acknowledgement.At;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReplicaAcknowledgement>> GetAsync(string replica, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            $"SELECT replica, account, collection, scope, [checkpoint], acknowledged_at FROM {_table} WHERE replica = @replica ORDER BY id",
            connection);
        command.Parameters.Add("@replica", SqlDbType.NVarChar, 256).Value = replica;
        var results = new List<ReplicaAcknowledgement>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ReplicaAcknowledgement(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDateTimeOffset(5)));
        }

        return results;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!_exists)
        {
            await _created.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_exists)
                {
                    await using var create = new SqlCommand(
                        $"""
                        IF SCHEMA_ID(N'{Schema}') IS NULL EXEC(N'CREATE SCHEMA [{Schema}]');
                        IF OBJECT_ID(N'{_table}', N'U') IS NULL
                        BEGIN
                            CREATE TABLE {_table} (
                                id bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_replica_acknowledgements] PRIMARY KEY,
                                replica nvarchar(256) NOT NULL,
                                account nvarchar(256) NULL,
                                collection nvarchar(256) NOT NULL,
                                scope nvarchar(256) NOT NULL,
                                [checkpoint] nvarchar(1024) NOT NULL,
                                acknowledged_at datetimeoffset NOT NULL);
                            CREATE INDEX [IX_replica_acknowledgements_replica] ON {_table} (replica, collection, scope, id);
                        END
                        """,
                        connection);
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    _exists = true;
                }
            }
            finally
            {
                _created.Release();
            }
        }
    }
}
