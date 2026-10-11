using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bsync.Documents;

/// <summary>
/// Produces independent copies and canonical fingerprints of documents. The engine and stores keep
/// current, base and pending states isolated, so mutating one must never alias another.
/// </summary>
/// <remarks>
/// For trimmed or AOT-compiled apps (Blazor WebAssembly publish, native AOT) use the overloads that take
/// a source-generated <see cref="JsonTypeInfo{T}"/>, or a hand-written clone. The reflection-based
/// overloads are annotated so the trimming analyzer reports their use.
/// </remarks>
public static class DocumentCloner
{
    /// <summary>
    /// Returns a deep copy of <paramref name="document"/> via reflection-based JSON round-trip. Returns
    /// <see langword="null"/> when <paramref name="document"/> is <see langword="null"/>.
    /// </summary>
    [RequiresUnreferencedCode("Uses reflection-based JSON serialization to clone documents. Use Json(JsonTypeInfo<T>) or a hand-written cloner for trimmed or AOT targets.")]
    [RequiresDynamicCode("Uses reflection-based JSON serialization to clone documents. Use Json(JsonTypeInfo<T>) or a hand-written cloner for trimmed or AOT targets.")]
    [return: NotNullIfNotNull(nameof(document))]
    public static T? JsonClone<T>(T? document)
        where T : class
    {
        if (document is null)
        {
            return null;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(document);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"Failed to clone document of type {typeof(T).Name}.");
    }

    /// <summary>Returns a trim/AOT-safe deep-clone function based on source-generated JSON metadata.</summary>
    public static Func<T, T> Json<T>(JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return document =>
        {
            // The JSON goes through a buffer and writer reused per thread: a clone allocates only the new document (D9).
            var buffer = ScratchJson.Write(document, typeInfo);
            var clone = JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo)
                ?? throw new InvalidOperationException($"Failed to clone document of type {typeof(T).Name}.");
            ScratchJson.Release(buffer);
            return clone;
        };
    }

    /// <summary>
    /// Returns a trim/AOT-safe fingerprint function (the document's JSON text) for detecting an operation
    /// id reused with a different payload.
    /// </summary>
    public static Func<T, string> JsonFingerprint<T>(JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new JsonFingerprint<T>(typeInfo).Compute;
    }
}

/// <summary>
/// The function returned by <see cref="DocumentCloner.JsonFingerprint{T}"/>. The in-memory server recognizes it and hashes
/// the JSON bytes directly instead of building the text (D9); the digest is the same.
/// </summary>
internal sealed class JsonFingerprint<T>(JsonTypeInfo<T> typeInfo)
    where T : class
{
    public string Compute(T document) => JsonSerializer.Serialize(document, typeInfo);

    /// <summary>Appends the UTF-8 JSON of <paramref name="document"/> (the bytes of <see cref="Compute"/>'s text) to <paramref name="hash"/>.</summary>
    public void AppendTo(System.Security.Cryptography.IncrementalHash hash, T document)
    {
        var buffer = ScratchJson.Write(document, typeInfo);
        hash.AppendData(buffer.WrittenSpan);
        ScratchJson.Release(buffer);
    }
}

/// <summary>A JSON buffer and writer reused per thread (D9).</summary>
internal static class ScratchJson
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_buffer;

    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    /// <summary>Serializes into the thread's buffer. Call <see cref="Release"/> when done with the bytes.</summary>
    public static ArrayBufferWriter<byte> Write<T>(T document, JsonTypeInfo<T> typeInfo)
    {
        // Taken while in use, so a nested use on the same thread (a converter that clones) gets its own buffer.
        var buffer = t_buffer ?? new ArrayBufferWriter<byte>(4096);
        var writer = t_writer ?? new Utf8JsonWriter(buffer);
        t_buffer = null;
        t_writer = null;
        buffer.ResetWrittenCount();
        writer.Reset(buffer);
        JsonSerializer.Serialize(writer, document, typeInfo);
        writer.Reset(); // detaches nothing, but leaves no pending state behind
        t_writer = writer;
        return buffer;
    }

    /// <summary>Returns the buffer for reuse, unless it grew unusually large.</summary>
    public static void Release(ArrayBufferWriter<byte> buffer)
    {
        if (buffer.Capacity <= 1024 * 1024)
        {
            t_buffer = buffer;
        }
    }
}
