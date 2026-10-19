using System.Security.Cryptography;
using System.Text;
using Bsync.Blobs;
using Xunit;

namespace Bsync.Tests;

public sealed class StreamingSha256Tests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    public void StandardVectorsAndReset(string text, string expected)
    {
        using var hash = new StreamingSha256(forceManaged: true);
        foreach (var value in Encoding.ASCII.GetBytes(text)) hash.AppendData(new[] { value });
        Assert.Equal(expected, Convert.ToHexStringLower(hash.GetHashAndReset()));
        hash.AppendData(Encoding.ASCII.GetBytes(text));
        Assert.Equal(expected, Convert.ToHexStringLower(hash.GetHashAndReset()));
        Assert.Equal(SHA256.HashData([]), hash.GetHashAndReset());
    }

    [Fact]
    public void PaddingAndFragmentationAgreeWithIndependentPlatformDigest()
    {
        var random = new Random(1804);
        foreach (var size in Enumerable.Range(0, 260).Concat([1023, 1024, 1025, 65535, 65536, 65537, 1048577]))
        {
            var bytes = new byte[size]; random.NextBytes(bytes);
            using var hash = new StreamingSha256(forceManaged: true);
            var offset = 0;
            while (offset < size)
            {
                var count = Math.Min(random.Next(1, 129), size - offset);
                hash.AppendData(bytes, offset, count); hash.AppendData(ReadOnlySpan<byte>.Empty); offset += count;
            }
            Assert.Equal(SHA256.HashData(bytes), hash.GetHashAndReset());
        }
    }

    [Fact]
    public void MillionAStandardVector()
    {
        using var hash = new StreamingSha256(forceManaged: true);
        var block = Enumerable.Repeat((byte)'a', 1000).ToArray();
        for (var i = 0; i < 1000; i++) hash.AppendData(block);
        Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0", Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    [Fact]
    public void LengthBeyond32BitBitCountHasBoundedAllocationsAndMatchesPlatform()
    {
        var block = new byte[1048576]; new Random(411).NextBytes(block);
        using var hash = new StreamingSha256(forceManaged: true);
        using var platform = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(block); hash.GetHashAndReset(); // Warm the compression implementation before measuring allocations.
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 513; i++) { hash.AppendData(block); platform.AppendData(block); }
        var increase = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(platform.GetHashAndReset(), hash.GetHashAndReset());
        Assert.True(increase < 4096, $"Streaming compression allocated {increase} bytes.");
    }

    [Fact]
    public void DisposedStateCannotBeReused()
    {
        var hash = new StreamingSha256(forceManaged: true); hash.AppendData([1, 2, 3]); hash.Dispose(); hash.Dispose();
        Assert.Throws<ObjectDisposedException>(() => hash.AppendData([4]));
        Assert.Throws<ObjectDisposedException>(() => hash.GetHashAndReset());
    }
}
