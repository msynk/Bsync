using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bsync.Tests.Sqlite;

/// <summary>ADR-018 in SQLite: index rows are maintained and used, rebuilt when the declared set changes, and never trusted when stale.</summary>
public sealed class SqliteIndexTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private static RecordUpdate<ConformanceDocument> Put(string id, string title, bool dirty = false) =>
        new(id, _ => new SyncRecord<ConformanceDocument>(new ConformanceDocument { Id = id, Title = title }, dirty ? null : new ConformanceDocument { Id = id, Title = title }, dirty) { BaseVersion = dirty ? null : 1, LocalRevision = dirty ? 1 : 0 });

    private static async Task<string[]> Ids(ILocalStore<ConformanceDocument> store, SyncIndexQuery<ConformanceDocument> query) =>
        [.. (await store.QueryIndexAsync(query, null, 100)).Select(d => d.Id)];

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_database.Path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact(DisplayName = "ADR-018: queries read the index rows (one per declared index and live record) instead of the collection")]
    public async Task RowsAreMaintainedAndUsed()
    {
        var store = await _database.OpenConformanceAsync(LocalStoreIndexConformance.Indexes);
        await store.UpdateAsync([Put("a", "x"), Put("b", "y"), Put("c", "z")]);
        await store.UpdateAsync([new("c", r => r! with { Current = new ConformanceDocument { Id = "c", Title = "z", Deleted = true } })]);

        Assert.Equal(6, await ScalarAsync("SELECT COUNT(*) FROM bs_index")); // 2 live records × 3 indexes; the tombstone has none
        await ScalarAsync("DELETE FROM bs_index WHERE name = 'title' AND id_key = CAST(X'0062' AS BLOB)");

        Assert.Equal(["a"], await Ids(store, LocalStoreIndexConformance.Title.All())); // "b" lost its row, so the index is what was read
        Assert.Equal(1, await store.CountIndexAsync(LocalStoreIndexConformance.Title.All()));
    }

    [Fact(DisplayName = "ADR-018 I17: a schema 4 database upgrades in place; keys are built on open and pending work is kept")]
    public async Task Schema4Upgrades()
    {
        var plain = await _database.OpenConformanceAsync();
        await plain.UpdateAsync([Put("a", "b"), Put("b", "a", dirty: true)]);
        SqliteStorePool.Release(_database.Path);
        await ScalarAsync("DROP TABLE bs_index; ALTER TABLE bs_records DROP COLUMN rejection_arguments; DELETE FROM bs_meta WHERE key = 'indexes'; PRAGMA user_version = 4; SELECT 0");

        var indexed = await _database.OpenConformanceAsync(LocalStoreIndexConformance.Indexes);

        Assert.Equal(SqliteLocalStore<ConformanceDocument>.SchemaVersion, (int)await ScalarAsync("PRAGMA user_version"));
        Assert.Equal(["b", "a"], await Ids(indexed, LocalStoreIndexConformance.Title.All()));
        Assert.Equal(6, await ScalarAsync("SELECT COUNT(*) FROM bs_index"));
        Assert.True((await indexed.GetAsync("b"))!.IsDirty);
        Assert.Equal(1, await indexed.CountDirtyAsync());
    }

    [Fact(DisplayName = "ADR-018: a new index version rebuilds the keys on open")]
    public async Task VersionChangeRebuilds()
    {
        var v1 = SyncIndex<ConformanceDocument>.Create("rank", d => d.Title, version: 1);
        var store = await _database.OpenConformanceAsync([v1]);
        await store.UpdateAsync([Put("a", "1"), Put("b", "2"), Put("c", "3")]);
        Assert.Equal(["a", "b", "c"], await Ids(store, v1.All()));

        var v2 = SyncIndex<ConformanceDocument>.Create("rank", d => -int.Parse(d.Title, System.Globalization.CultureInfo.InvariantCulture), version: 2);
        var reopened = await _database.OpenConformanceAsync([v2]);

        Assert.Equal(["c", "b", "a"], await Ids(reopened, v2.All()));
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM bs_index"));
    }

    [Fact(DisplayName = "ADR-018 I02: a writer that does not maintain the indexes makes them unusable; queries stay correct, and the next indexed open rebuilds")]
    public async Task ForeignWriterInvalidates()
    {
        var indexed = await _database.OpenConformanceAsync(LocalStoreIndexConformance.Indexes);
        await indexed.UpdateAsync([Put("a", "b")]);
        var plain = await _database.OpenConformanceAsync();
        await plain.UpdateAsync([Put("b", "a")]);

        Assert.Equal(["b", "a"], await Ids(indexed, LocalStoreIndexConformance.Title.All())); // evaluated in memory, still complete
        await ScalarAsync("DELETE FROM bs_index");
        Assert.Equal(["b", "a"], await Ids(indexed, LocalStoreIndexConformance.Title.All())); // the rows are not used any more

        var rebuilt = await _database.OpenConformanceAsync(LocalStoreIndexConformance.Indexes);
        Assert.Equal(6, await ScalarAsync("SELECT COUNT(*) FROM bs_index"));
        Assert.Equal(["b", "a"], await Ids(rebuilt, LocalStoreIndexConformance.Title.All()));
    }
}
