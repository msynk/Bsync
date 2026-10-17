using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Blazor.IndexedDb;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Storage;
using Bsync.Transport;
using Microsoft.JSInterop;

namespace Bsync.Tests.BrowserHost;

public static partial class Harness
{
    [JSInvokable(nameof(PublishedMigration))]
    public static async Task<string> PublishedMigration(string databaseName, string originalRecords)
    {
        using var original = JsonDocument.Parse(originalRecords);
        var ids = original.RootElement.EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!,
            row => row.TryGetProperty("pendingId", out var id) ? id.GetString() : null);
        await using var store = await IndexedDbLocalStore<MigrationDocument>.OpenAsync(_js,
            new() { DatabaseName = databaseName, Collection = "fixtures" }, MigrationJson.Default.MigrationDocument);
        var pending = (await store.GetAsync("pending"))!;
        var kept = (await store.GetAsync("kept"))!;
        var rejected = (await store.GetAsync("rejected"))!;
        var group = (await store.GetAsync("g1"))!;
        var colour = (await store.GetAsync("colour"))!;
        var cursor = await store.GetCursorAsync();
        if (cursor.Checkpoint.Value is null || !pending.IsDirty || pending.Pending?.OperationId != ids["pending"]
            || kept.Conflict?.Server.Title != "theirs" || kept.Conflict.Local.Title != "mine"
            || rejected.Rejection?.ErrorCode != "title-forbidden" || group.Group?.Members.Count != 2
            || colour.Current.Unknown?["colour"].GetString() != "teal" || colour.Current.Amount != 12.5m
            || await store.CountDirtyAsync() != 4 || await ((ILocalStore<MigrationDocument>)store).CountIssuesAsync() != new SyncIssueCounts(1, 1, 0))
            throw new InvalidDataException("Published store state was not preserved.");
        var transport = new MigrationTransport(cursor.Checkpoint);
        var engine = new SyncEngine<MigrationDocument>(store, transport, new HybridLogicalClock("migration-current"),
            DocumentCloner.Json(MigrationJson.Default.MigrationDocument));
        await engine.SyncAsync();
        var sent = transport.Pushes.SelectMany(request => request.Operations).ToDictionary(operation => operation.DocumentId);
        if (!sent.Keys.Order().SequenceEqual(new[] { "g1", "g2", "pending" })
            || sent.Any(pair => pair.Value.OperationId != ids[pair.Key])
            || !transport.Pushes.Any(request => request.Operations.Count(operation => operation.DocumentId is "g1" or "g2") == 2)
            || sent["pending"].Document.Unknown?["colour"].GetString() != "amber"
            || (await store.GetAsync("rejected"))!.Rejection?.ErrorCode != "title-forbidden"
            || (await store.GetAsync("kept"))!.Conflict is null)
            throw new InvalidDataException("Published pending operations changed during transmission.");
        return "ok";
    }

    private sealed class MigrationTransport(Checkpoint checkpoint) : ISyncTransport<MigrationDocument>
    {
        private long _version = 100;
        public List<PushRequest<MigrationDocument>> Pushes { get; } = [];
        public Task<PullResult<MigrationDocument>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PullResult<MigrationDocument>([], request.Since.Value is null ? checkpoint : request.Since, false) { Features = [SyncFeatures.Groups] });
        public Task<PushResult<MigrationDocument>> PushAsync(PushRequest<MigrationDocument> request, CancellationToken cancellationToken = default)
        {
            Pushes.Add(request);
            return Task.FromResult(new PushResult<MigrationDocument>([.. request.Operations.Select(operation =>
                PushOutcome<MigrationDocument>.Accepted(operation.OperationId, ++_version, operation.Document))]));
        }
        public IAsyncEnumerable<StreamEvent<MigrationDocument>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

public sealed class MigrationDocument : ISyncEntity
{
    public string Id { get; set; } = "";
    public HlcTimestamp UpdatedAt { get; set; }
    public bool Deleted { get; set; }
    public string? Title { get; set; }
    public decimal Amount { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MigrationDocument))]
internal sealed partial class MigrationJson : JsonSerializerContext;
