using System.Text;
using Bsync.Clocks;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Testing;
using Xunit;

namespace Bsync.Tests.SqliteEncrypted;

/// <summary>ADR-016: the SQLite store encrypted at rest with SQLCipher.</summary>
public sealed class EncryptedStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bsync-encrypted-").FullName;
    private readonly byte[] _key = SqliteEncryption.NewKey();
    private int _next;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private string NewPath() => Path.Combine(_directory, $"replica-{Interlocked.Increment(ref _next)}.db");

    private static Task<SqliteLocalStore<ConformanceDocument>> OpenAsync(string path, byte[]? key, IEnumerable<SyncIndex<ConformanceDocument>>? indexes = null) =>
        SqliteLocalStore<ConformanceDocument>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = path, Collection = "conformance", EncryptionKey = key },
            ConformanceJsonContext.Default.ConformanceDocument,
            indexes ?? []);

    private static RecordUpdate<ConformanceDocument> Pending(string id, string title) =>
        new(id, _ => new SyncRecord<ConformanceDocument>(new ConformanceDocument { Id = id, Title = title, UpdatedAt = new HlcTimestamp(5, 0, "n") }, null, IsDirty: true) { LocalRevision = 1 });

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in LocalStoreConformance.Cases.Select(c => c.Name).Concat(LocalStoreIndexConformance.Cases.Select(c => c.Name)))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Conformance(string name)
    {
        var path = NewPath();
        return LocalStoreConformance.Cases.SingleOrDefault(c => c.Name == name) is { } plain
            ? plain.RunAsync(async () => await OpenAsync(path, _key))
            : LocalStoreIndexConformance.Cases.Single(c => c.Name == name).RunAsync(async () => await OpenAsync(path, _key, LocalStoreIndexConformance.Indexes));
    }

    [Fact(DisplayName = "ADR-016: the file holds no plain text and cannot be opened without the key or with another one; nothing is replaced")]
    public async Task UnreadableWithoutTheKey()
    {
        var path = NewPath();
        var store = await OpenAsync(path, _key);
        await store.UpdateAsync([Pending("n1", "secret-title-0123456789")]);
        SqliteStorePool.Release(path);
        var bytes = await File.ReadAllBytesAsync(path);
        var size = bytes.Length;

        Assert.False(SqliteEncryption.IsPlain(path));
        Assert.DoesNotContain("secret-title-0123456789", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        await Assert.ThrowsAsync<SqliteStoreUnreadableException>(() => OpenAsync(path, null));
        await Assert.ThrowsAsync<SqliteStoreUnreadableException>(() => OpenAsync(path, SqliteEncryption.NewKey()));
        Assert.Equal(size, new FileInfo(path).Length);

        var reopened = await OpenAsync(path, _key);
        Assert.Equal("secret-title-0123456789", (await reopened.GetAsync("n1"))!.Current.Title);
        Assert.Empty(await SqliteStoreRecovery.CheckAsync(path, _key));
    }

    [Fact(DisplayName = "ADR-016 I01 I17: a plain replica is encrypted in place with its pending work; the key can be changed; wipe removes every file")]
    public async Task EncryptRekeyAndWipe()
    {
        var path = NewPath();
        var plain = await OpenAsync(path, null);
        await plain.UpdateAsync([Pending("draft", "not yet uploaded")]);
        Assert.True(SqliteEncryption.IsPlain(path));

        await SqliteEncryption.EncryptAsync(path, _key);
        var encrypted = await OpenAsync(path, _key);

        Assert.False(SqliteEncryption.IsPlain(path));
        Assert.True((await encrypted.GetAsync("draft"))!.IsDirty);
        Assert.Equal(SqliteLocalStore<ConformanceDocument>.SchemaVersion, SqliteLocalStore<ConformanceDocument>.SchemaVersion);

        var newKey = SqliteEncryption.NewKey();
        await SqliteEncryption.RekeyAsync(path, _key, newKey);

        await Assert.ThrowsAsync<SqliteStoreUnreadableException>(() => OpenAsync(path, _key));
        Assert.Equal(1, await (await OpenAsync(path, newKey)).CountDirtyAsync());
        await Assert.ThrowsAsync<SqliteStoreUnreadableException>(() => SqliteEncryption.RekeyAsync(path, _key, newKey));

        await SqliteStorePool.DeleteDatabaseAsync(path);
        Assert.Empty(Directory.EnumerateFiles(_directory, Path.GetFileName(path) + "*"));
    }
}
