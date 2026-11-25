using System.Collections;
using System.Reflection;
using Bsync.Clocks;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bsync.Tests.SqliteEncrypted;

/// <summary>
/// Plan item A10: once a replica is released or deleted (for example at sign-out), no process-wide structure of Bsync or
/// of Microsoft.Data.Sqlite still holds its SQLCipher key.
/// </summary>
public sealed class KeyRetentionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bsync-keys-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Every string reachable (a few levels deep) from the static fields of <paramref name="types"/>.</summary>
    internal static readonly List<string> Roots = [];

    private static List<string> ReachableStrings(IEnumerable<Type> types)
    {
        Roots.Clear();
        var found = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? value, int depth)
        {
            if (value is null || depth > 6 || value is Type || value is Delegate || value is Assembly)
            {
                return;
            }

            if (value is string text)
            {
                found.Add(text);
                return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || !seen.Add(value))
            {
                return;
            }

            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    Walk(item, depth + 1);
                }
            }

            for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsPointer)
                    {
                        Walk(field.GetValue(value), depth + 1);
                    }
                }
            }
        }

        foreach (var type in types)
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!type.ContainsGenericParameters && !field.FieldType.IsPointer)
                {
                    var before = found.Count;
                    Walk(field.GetValue(null), 0);
                    for (var i = before; i < found.Count; i++)
                    {
                        Roots.Add($"{type.FullName}.{field.Name}");
                    }
                }
            }
        }

        return found;
    }

    private static List<string> ProcessWideStrings() =>
        ReachableStrings(typeof(SqliteStorePool).Assembly.GetTypes().Concat(typeof(SqliteConnection).Assembly.GetTypes()));

    [Fact(DisplayName = "A10: deleting an encrypted replica leaves its key in no process-wide pool or cache")]
    public async Task DeleteForgetsKey()
    {
        var key = SqliteEncryption.NewKey();
        var hex = Convert.ToHexString(key);
        var path = Path.Combine(_directory, "replica.db");
        var store = await SqliteLocalStore<ConformanceDocument>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = path, Collection = "conformance", EncryptionKey = key },
            ConformanceJsonContext.Default.ConformanceDocument, []);
        await store.UpdateAsync([new("d", _ => new SyncRecord<ConformanceDocument>(new ConformanceDocument { Id = "d", UpdatedAt = new HlcTimestamp(5, 0, "n") }, null, IsDirty: true) { LocalRevision = 1 })]);
        Assert.Contains(ProcessWideStrings(), s => s.Contains(path, StringComparison.OrdinalIgnoreCase)); // the probe reaches the pool registry
        Assert.DoesNotContain(ProcessWideStrings(), s => s.Contains(hex, StringComparison.OrdinalIgnoreCase)); // not even while open

        await SqliteStorePool.DeleteDatabaseAsync(path);

        var strings = ProcessWideStrings();
        var holders = strings.Select((s, i) => (s, i)).Where(x => x.s.Contains(hex, StringComparison.OrdinalIgnoreCase)).Select(x => Roots[x.i]).Distinct();
        Assert.True(!holders.Any(), string.Join(", ", holders));
    }

    [Fact(DisplayName = "A10: releasing an encrypted replica's pool leaves its key in no process-wide pool or cache")]
    public async Task ReleaseForgetsKey()
    {
        var key = SqliteEncryption.NewKey();
        var hex = Convert.ToHexString(key);
        var path = Path.Combine(_directory, "released.db");
        var store = await SqliteLocalStore<ConformanceDocument>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = path, Collection = "conformance", EncryptionKey = key },
            ConformanceJsonContext.Default.ConformanceDocument, []);
        Assert.Equal(0, await store.CountDirtyAsync());

        SqliteStorePool.Release(path);

        var strings = ProcessWideStrings();
        Assert.DoesNotContain(strings, s => s.Contains(hex, StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "A10: a pooled connection keyed for one key is never reused for another key or for a plain open")]
    public async Task PooledConnectionsAreNotSharedAcrossKeys()
    {
        var key = SqliteEncryption.NewKey();
        var path = Path.Combine(_directory, "shared.db");
        var right = await SqliteLocalStore<ConformanceDocument>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = path, Collection = "conformance", EncryptionKey = key },
            ConformanceJsonContext.Default.ConformanceDocument, []);
        Assert.Equal(0, await right.CountDirtyAsync()); // leaves a keyed connection in the pool

        await Assert.ThrowsAsync<SqliteStoreUnreadableException>(() => SqliteLocalStore<ConformanceDocument>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = path, Collection = "conformance", EncryptionKey = SqliteEncryption.NewKey() },
            ConformanceJsonContext.Default.ConformanceDocument, []));
        await Assert.ThrowsAsync<SqliteStoreUnreadableException>(() => SqliteLocalStore<ConformanceDocument>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = path, Collection = "conformance" },
            ConformanceJsonContext.Default.ConformanceDocument, []));
        Assert.Equal(0, await right.CountDirtyAsync()); // and the right key still works afterwards
    }
}
