using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Bsync.Blobs;

// .NET's browser SHAManagedHashProvider buffers all incremental input for one-shot managed hashing.
// Keep full-content verification bounded on WASM; native targets retain their platform implementation.
// SHA-256 compression/padding follows FIPS 180-4 sections 5.1.1, 5.2.1, 5.3.3 and 6.2.
internal sealed class StreamingSha256 : IDisposable
{
    private readonly IncrementalHash? _native;
    private readonly uint[] _state = new uint[8];
    private readonly uint[] _words = new uint[64];
    private readonly byte[] _tail = new byte[64];
    private ulong _bytes;
    private int _tailLength;
    private bool _disposed;
    private static ReadOnlySpan<uint> Constants => [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2];

    internal StreamingSha256(bool forceManaged = false)
    {
        if (!forceManaged && !OperatingSystem.IsBrowser()) _native = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Reset();
    }

    public void AppendData(byte[] bytes, int offset, int count) => AppendData(bytes.AsSpan(offset, count));
    public void AppendData(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_native is not null) { _native.AppendData(bytes); return; }
        if ((ulong)bytes.Length > (ulong.MaxValue >> 3) - _bytes) throw new CryptographicException("SHA-256 input length limit exceeded.");
        _bytes += (ulong)bytes.Length;
        if (_tailLength != 0)
        {
            var count = Math.Min(64 - _tailLength, bytes.Length);
            bytes[..count].CopyTo(_tail.AsSpan(_tailLength)); _tailLength += count; bytes = bytes[count..];
            if (_tailLength != 64) return;
            Compress(_tail); _tailLength = 0;
        }
        while (bytes.Length >= 64) { Compress(bytes[..64]); bytes = bytes[64..]; }
        bytes.CopyTo(_tail); _tailLength = bytes.Length;
    }

    public byte[] GetHashAndReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_native is not null) return _native.GetHashAndReset();
        _tail[_tailLength++] = 0x80;
        if (_tailLength > 56)
        {
            _tail.AsSpan(_tailLength).Clear(); Compress(_tail); _tailLength = 0;
        }
        _tail.AsSpan(_tailLength, 56 - _tailLength).Clear();
        BinaryPrimitives.WriteUInt64BigEndian(_tail.AsSpan(56), _bytes << 3); Compress(_tail);
        var result = new byte[32];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), _state[i]);
        Reset(); return result;
    }

    private void Reset()
    {
        _state[0] = 0x6a09e667; _state[1] = 0xbb67ae85; _state[2] = 0x3c6ef372; _state[3] = 0xa54ff53a;
        _state[4] = 0x510e527f; _state[5] = 0x9b05688c; _state[6] = 0x1f83d9ab; _state[7] = 0x5be0cd19;
        _bytes = 0; _tailLength = 0; Array.Clear(_words); CryptographicOperations.ZeroMemory(_tail);
    }

    private void Compress(ReadOnlySpan<byte> block)
    {
        unchecked
        {
            var words = _words.AsSpan();
            for (var i = 0; i < 16; i++) words[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
            for (var i = 16; i < 64; i++)
            {
                var x = words[i - 15]; var y = words[i - 2];
                words[i] = words[i - 16] + (BitOperations.RotateRight(x, 7) ^ BitOperations.RotateRight(x, 18) ^ (x >> 3)) +
                    words[i - 7] + (BitOperations.RotateRight(y, 17) ^ BitOperations.RotateRight(y, 19) ^ (y >> 10));
            }
            var a = _state[0]; var b = _state[1]; var c = _state[2]; var d = _state[3];
            var e = _state[4]; var f = _state[5]; var g = _state[6]; var h = _state[7];
            var constants = Constants;
            for (var i = 0; i < 64; i++)
            {
                var first = h + (BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25)) +
                    ((e & f) ^ (~e & g)) + constants[i] + words[i];
                var second = (BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22)) +
                    ((a & b) ^ (a & c) ^ (b & c));
                h = g; g = f; f = e; e = d + first; d = c; c = b; b = a; a = first + second;
            }
            _state[0] += a; _state[1] += b; _state[2] += c; _state[3] += d;
            _state[4] += e; _state[5] += f; _state[6] += g; _state[7] += h;
        }
    }

    public static async Task<byte[]> HashDataAsync(Stream stream, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stream); token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsBrowser()) return await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        using var hash = new StreamingSha256();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, 65536), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (count == 0) return hash.GetHashAndReset();
                if (count < 0 || count > 65536) throw new IOException("Invalid content stream read count.");
                hash.AppendData(buffer.AsSpan(0, count));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _native?.Dispose(); Array.Clear(_state); Array.Clear(_words); CryptographicOperations.ZeroMemory(_tail);
        _bytes = 0; _tailLength = 0; _disposed = true;
    }
}
