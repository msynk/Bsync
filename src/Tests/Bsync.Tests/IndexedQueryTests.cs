using Bsync.Client;
using Bsync.Clocks;
using Bsync.Storage;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>ADR-018 through the collection API: index ranges, residual filters, skip, count, and in-memory evaluation.</summary>
public sealed class IndexedQueryTests
{
    private static readonly SyncIndex<Note, string> ByTitle = SyncIndex<Note>.Create("title", n => n.Title);

    private static async Task<(SyncSession<Note> Session, LocalSyncCollection<Note> Notes)> CollectionAsync()
    {
        var server = InMemorySyncServerRef.Create(SystemPhysicalClock.Instance);
        var session = new SyncSession<Note>(new SyncSessionOptions<Note>
        {
            Cloner = NoteJson.Clone,
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Note>(new InMemoryLocalStore<Note>(NoteJson.Clone, [ByTitle]), "n")),
            CreateTransport = _ => new ServerRefTransport(server),
            Interval = TimeSpan.FromMinutes(10),
        });
        var notes = new LocalSyncCollection<Note>(session, _ => Task.FromResult("alice"));
        for (var i = 0; i < 450; i++)
        {
            await notes.SaveAsync(new Note { Id = $"n{i:D3}", Title = $"t{i % 30:D2}", Body = i % 2 == 0 ? "even" : "odd" });
        }

        await notes.DeleteAsync("n001");
        return (session, notes);
    }

    [Fact(DisplayName = "ADR-018: an index range with a residual filter, skip and limit pages through the store; counts match")]
    public async Task RangeFilterSkipCount()
    {
        var (session, notes) = await CollectionAsync();
        await using var _ = session;

        var query = new SyncQuery<Note> { Index = ByTitle.Between("t05", "t09").Descending(), Where = n => n.Body == "even", Skip = 10, Limit = 20 };
        var page = await notes.QueryAsync(query);
        var expected = Enumerable.Range(0, 450)
            .Where(i => i != 1 && i % 30 is >= 5 and <= 9 && i % 2 == 0)
            .Select(i => (Title: $"t{i % 30:D2}", Id: $"n{i:D3}"))
            .OrderByDescending(e => e.Title, StringComparer.Ordinal).ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected.Skip(10).Take(20).Select(e => e.Id), page.Select(n => n.Id));
        Assert.Equal(expected.Count, await notes.CountAsync(query));
        Assert.Equal(449, await notes.CountAsync(new SyncQuery<Note> { Index = ByTitle.All() })); // one deleted
        Assert.Equal(15, await notes.CountAsync(new SyncQuery<Note> { Index = ByTitle.Equal("t05") }));
        Assert.Equal(225, await notes.CountAsync(new SyncQuery<Note> { Where = n => n.Body == "even" }));
        Assert.Equal(["n447", "n448", "n449"], (await notes.QueryAsync(new SyncQuery<Note> { Skip = 446, Limit = 10 })).Select(n => n.Id)); // id order, deleted skipped
        await Assert.ThrowsAsync<ArgumentException>(() => notes.QueryAsync(new SyncQuery<Note> { Index = ByTitle.All(), Order = (a, b) => 0 }));
    }

    [Fact(DisplayName = "ADR-018: in-memory evaluation (server-connected collections) gives the same order, range and skip")]
    public void ApplyMatchesStoreOrder()
    {
        var documents = new[] { "b", "a", "c", "b" }.Select((t, i) => new Note { Id = $"x{i}", Title = t }).Append(new Note { Id = "gone", Title = "b", Deleted = true });
        var query = new SyncQuery<Note> { Index = ByTitle.From("b").Descending(), Skip = 1, Limit = 2 };

        Assert.Equal(["x3", "x0"], query.Apply(documents).Select(n => n.Id));
    }
}
