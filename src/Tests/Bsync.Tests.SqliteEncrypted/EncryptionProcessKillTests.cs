using System.Diagnostics;
using System.Text;
using Bsync.Clocks;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bsync.Tests.SqliteEncrypted;

/// <summary>
/// Plan item A11: encrypting a plain replica that still has a write-ahead log is killed (a real process termination) at
/// each durable step. Afterwards the replica is either still plain and complete, or encrypted and complete, never an
/// encrypted file beside a plain log; finishing the encryption then leaves no plain text in any file.
/// </summary>
public sealed class EncryptionProcessKillTests : IDisposable
{
    private const string Marker = "plaintext-marker-";
    private const int Records = 40;
    private readonly string _directory = Directory.CreateTempSubdirectory("bsync-encrypt-kill-").FullName;
    private readonly byte[] _key = SqliteEncryption.NewKey();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static Task<SqliteLocalStore<ConformanceDocument>> OpenAsync(string path, byte[]? key) =>
        SqliteLocalStore<ConformanceDocument>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = path, Collection = "conformance", EncryptionKey = key },
            ConformanceJsonContext.Default.ConformanceDocument, []);

    /// <summary>A plain replica as a crash leaves it: recent writes only in its write-ahead log.</summary>
    private async Task<string> CrashedPlainReplicaAsync(string name)
    {
        var live = Path.Combine(_directory, name + "-live.db");
        var store = await OpenAsync(live, null);
        await using (var connection = new SqliteConnection($"Data Source={live}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_autocheckpoint = 0";
            await command.ExecuteNonQueryAsync();
            await store.UpdateAsync([.. Enumerable.Range(0, Records).Select(i => new RecordUpdate<ConformanceDocument>($"d{i:D3}", _ =>
                new SyncRecord<ConformanceDocument>(new ConformanceDocument { Id = $"d{i:D3}", Title = Marker + i, UpdatedAt = new HlcTimestamp(5, 0, "n") }, null, IsDirty: true) { LocalRevision = 1 }))]);

            // Copy while the log is still open and unmerged, as after a kill.
            var crashed = Path.Combine(_directory, name + ".db");
            File.Copy(live, crashed);
            File.Copy(live + "-wal", crashed + "-wal");
            Assert.Contains(Marker, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(crashed + "-wal")), StringComparison.Ordinal);
            return crashed;
        }
    }

    private async Task<string> RunAndKillAsync(string path, string step)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.SqliteEncrypted.CrashHost.dll"), "encrypt", path, step, Convert.ToHexString(_key) })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var read = process.StandardOutput.ReadLineAsync(timeout.Token).AsTask();
            if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15))) != read)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The crash host did not reach " + step + ": " + await errors);
            }

            var line = await read;
            Assert.True(line == "reached " + step, line);
            return line!;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private async Task AssertCompleteAsync(string path, byte[]? key)
    {
        var store = await OpenAsync(path, key);
        Assert.Equal(Records, await store.CountDirtyAsync());
        Assert.Equal(Marker + (Records - 1), (await store.GetAsync($"d{Records - 1:D3}"))!.Current.Title);
        SqliteStorePool.Release(path);
    }

    private async Task<bool> AnyFileHoldsPlainTextAsync(string path)
    {
        foreach (var file in Directory.EnumerateFiles(_directory, Path.GetFileName(path) + "*"))
        {
            if (Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains(Marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [Theory(DisplayName = "A11: a kill at any step of encryption leaves a complete plain or encrypted replica and no plain log beside an encrypted one")]
    [InlineData("folded")]
    [InlineData("exported")]
    [InlineData("replaced")]
    public async Task EncryptionSurvivesKill(string step)
    {
        var path = await CrashedPlainReplicaAsync(step);

        await RunAndKillAsync(path, step);

        var plain = SqliteEncryption.IsPlain(path);
        Assert.Equal(step != "replaced", plain);
        Assert.False(File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0 && !plain, "An encrypted database sits beside a non-empty log.");
        if (step != "folded")
        {
            Assert.False(File.Exists(path + "-wal"), "The plain log outlived the export.");
        }

        if (plain)
        {
            await AssertCompleteAsync(path, null);
            await SqliteEncryption.EncryptAsync(path, _key); // finishing after the kill
        }

        Assert.False(SqliteEncryption.IsPlain(path));
        await AssertCompleteAsync(path, _key);
        Assert.False(File.Exists(path + ".encrypting"));
        Assert.False(await AnyFileHoldsPlainTextAsync(path), "Plain text remains in a file of the replica.");
    }

    [Fact(DisplayName = "A11: encrypting one replica leaves other databases' pooled connections alone")]
    public async Task OtherPoolsAreKept()
    {
        var other = $"Data Source={Path.Combine(_directory, "other.db")};Pooling=True";
        SQLitePCL.sqlite3 before;
        await using (var connection = new SqliteConnection(other))
        {
            await connection.OpenAsync();
            before = connection.Handle!;
        }

        var path = Path.Combine(_directory, "target.db");
        await (await OpenAsync(path, null)).CountDirtyAsync();
        await SqliteEncryption.EncryptAsync(path, _key);

        await using var after = new SqliteConnection(other);
        await after.OpenAsync();
        Assert.Same(before, after.Handle); // still pooled: only the encrypted file's pool was closed
    }
}
