using Bsync.Client;
using Xunit;

namespace Bsync.Tests;

public sealed class LocalKeyAccessTests
{
    [Fact]
    public void KeyOwnershipIsCopiedAndDisposalZeroesRetainedViews()
    {
        var reference = new LocalKeyReference(new("app", "test", "alice"), "replica", 2);
        var original = Enumerable.Repeat((byte)7, 32).ToArray();
        var access = LocalKeyAccess.Available(reference, original);
        var retained = access.Key;
        original[0] = 42;
        Assert.Equal(7, retained.Span[0]);
        Assert.Equal(reference, access.Reference);
        access.Dispose(); access.Dispose();
        Assert.All(retained.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(42, original[0]);
        Assert.Throws<ObjectDisposedException>(() => access.Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingKeyReportsExplicitStateWithoutReplacement(bool locked)
    {
        var reference = new LocalKeyReference(new("app", "test", "alice"), "replica", 1);
        using var access = locked ? LocalKeyAccess.Locked(reference) : LocalKeyAccess.Unavailable(reference);
        Assert.Equal(locked ? LocalKeyState.Locked : LocalKeyState.Unavailable, access.State);
        Assert.Equal(locked, Assert.Throws<ReplicaKeyUnavailableException>(() => access.Key).Locked);
        Assert.Throws<ArgumentException>(() => LocalKeyAccess.Available(reference, new byte[31]));
    }
}
