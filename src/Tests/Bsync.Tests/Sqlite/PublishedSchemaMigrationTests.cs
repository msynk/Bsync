using System.Text.Json;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bsync.Tests.Sqlite;

/// <summary>
/// Task G2: a replica written by the published 0.1.0 packages (Fixtures/replica-0.1.0.db, made by
/// src/Tests/Bsync.Fixtures.V010) opens with the current store and keeps everything.
/// </summary>
public sealed class PublishedSchemaMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bsync-v010-{Guid.NewGuid():N}.db");

    public PublishedSchemaMigrationTests() => File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replica-0.1.0.db"), _path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private Task<SqliteLocalStore<FixtureDoc>> OpenAsync() =>
        SqliteLocalStore<FixtureDoc>.OpenAsync(new SqliteLocalStoreOptions { DataSource = _path, Collection = "fixtures" }, FixtureJsonContext.Default.FixtureDoc);

    private async Task<Dictionary<string, string?>> OperationIdsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, pending_id FROM bs_records";
        var ids = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return ids;
    }

    [Fact(DisplayName = "G2 I01 I11 I17 I19: a 0.1.0 replica upgrades in place and keeps pending work, a kept conflict, a rejection, a group and unknown members")]
    public async Task UpgradesAndKeepsEverything()
    {
        var before = await OperationIdsAsync();
        var store = await OpenAsync();
        var cursor = await store.GetCursorAsync();

        var pending = (await store.GetAsync("pending"))!;
        var kept = (await store.GetAsync("kept"))!;
        var rejected = (await store.GetAsync("rejected"))!;
        var g1 = (await store.GetAsync("g1"))!;
        var colour = (await store.GetAsync("colour"))!;

        Assert.Equal(SqliteLocalStore<FixtureDoc>.SchemaVersion, await UserVersionAsync());
        Assert.NotNull(cursor.Checkpoint.Value);
        Assert.Equal((true, before["pending"]), (pending.IsDirty, pending.Pending!.OperationId));
        Assert.Equal(["a", "b"], pending.Current.Tags);
        Assert.Equal(("theirs", "mine"), (kept.Conflict!.Server.Title, kept.Conflict.Local.Title));
        Assert.Equal("title-forbidden", rejected.Rejection!.ErrorCode);
        Assert.Equal(2, g1.Group!.Members.Count);
        Assert.Equal("teal", colour.Current.Unknown!["colour"].GetString()); // a member this application does not know
        Assert.Equal(12.5m, colour.Current.Amount);
        Assert.Equal(4, await store.CountDirtyAsync());
        Assert.Equal(new Storage.SyncIssueCounts(1, 1, 0), await store.CountIssuesAsync());

        // The prepared operations go out unchanged: same ids, the group together, the unknown member kept.
        var transport = new Recording(cursor.Checkpoint);
        var engine = new SyncEngine<FixtureDoc>(store, new JsonWireTransport<FixtureDoc>(transport, FixtureJsonContext.Default), new Clocks.HybridLogicalClock("device-010"), DocumentCloner.Json(FixtureJsonContext.Default.FixtureDoc));
        var result = await engine.SyncAsync();

        var sent = transport.Pushes.SelectMany(p => p.Operations).ToDictionary(o => o.DocumentId);
        Assert.False(result.ResetPerformed);
        Assert.Equal(["g1", "g2", "pending"], sent.Keys.Order());
        Assert.Equal(before["pending"], sent["pending"].OperationId);
        Assert.Equal((before["g1"], before["g2"]), (sent["g1"].OperationId, sent["g2"].OperationId));
        Assert.Single(transport.Pushes, p => p.Operations.Any(o => o.DocumentId == "g1") && p.Operations.Any(o => o.DocumentId == "g2"));
        Assert.Equal("amber", sent["pending"].Document.Unknown!["colour"].GetString());
        Assert.Equal("title-forbidden", (await store.GetAsync("rejected"))!.Rejection!.ErrorCode); // still parked
        Assert.NotNull((await store.GetAsync("kept"))!.Conflict); // still kept for the user
    }

    private async Task<long> UserVersionAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>A server stand-in: answers pulls at the replica's checkpoint (nothing new) and accepts every push.</summary>
    private sealed class Recording(Checkpoint checkpoint) : ISyncTransport<FixtureDoc>
    {
        private long _version = 100;

        public List<PushRequest<FixtureDoc>> Pushes { get; } = [];

        public Task<PullResult<FixtureDoc>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PullResult<FixtureDoc>([], request.Since.Value is null ? checkpoint : request.Since, false) { Features = [SyncFeatures.Groups] });

        public Task<PushResult<FixtureDoc>> PushAsync(PushRequest<FixtureDoc> request, CancellationToken cancellationToken = default)
        {
            Pushes.Add(request);
            return Task.FromResult(new PushResult<FixtureDoc>([.. request.Operations.Select(o => PushOutcome<FixtureDoc>.Accepted(o.OperationId, ++_version, o.Document))]));
        }

        public IAsyncEnumerable<StreamEvent<FixtureDoc>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
