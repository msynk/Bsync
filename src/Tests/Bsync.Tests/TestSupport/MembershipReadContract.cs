using System.Security.Claims;
using Bsync.Server;
using Xunit;

namespace Bsync.Tests.TestSupport;

public static class MembershipReadContract
{
    private static SyncCallContext User(string? name) => new(new ClaimsPrincipal(new ClaimsIdentity(
        name is null ? [] : [new Claim(ClaimTypes.Name, name)], "test")), "team");

    public static async Task VerifyAsync(ISyncDocumentReader<Note> reader, ISyncPublisher<Note> publisher)
    {
        for (var i = 0; i < 5; i++) await publisher.UpsertAsync("team", new Note { Id = "a-hidden-" + i, Body = "bob" });
        await publisher.UpsertAsync("team", new Note { Id = "b-visible", Body = "alice" });
        await publisher.UpsertAsync("team", new Note { Id = "c-visible", Body = "alice" });
        Assert.Null(await reader.GetAsync(User("alice"), "a-hidden-0"));
        Assert.Null(await reader.GetAsync(User("bob"), "b-visible"));
        Assert.Null(await reader.GetAsync(User(null), "b-visible"));
        Assert.Empty(await reader.ListAsync(User(null), 10));
        Assert.Empty(await reader.ListAsync(User("Alice"), 10));
        Assert.Empty(await reader.ListAsync(User("alice "), 10));
        Assert.Equal(new[] { "b-visible", "c-visible" }, (await reader.ListAsync(User("alice"), 2)).Select(d => d.Document.Id));
        Assert.Equal("c-visible", Assert.Single(await reader.ListAsync(User("alice"), 2, "b-visible")).Document.Id);
        await publisher.UpsertAsync("team", new Note { Id = "b-visible", Body = "bob" });
        Assert.Null(await reader.GetAsync(User("alice"), "b-visible"));
        Assert.Equal("c-visible", Assert.Single(await reader.ListAsync(User("alice"), 2)).Document.Id);
        await publisher.DeleteAsync("team", "c-visible");
        Assert.True((await reader.GetAsync(User("alice"), "c-visible"))!.Document.Deleted);
        Assert.Null(await reader.GetAsync(User("bob"), "c-visible"));
        Assert.Empty(await reader.ListAsync(User("alice"), 2));
    }
}
