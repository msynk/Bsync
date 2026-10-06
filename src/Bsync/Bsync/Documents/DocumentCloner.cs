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
            // The JSON goes through a buffer reused per thread: a clone allocates only the new document (D9).
            var buffer = t_buffer ??= new ArrayBufferWriter<byte>(4096);
            buffer.ResetWrittenCount();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                JsonSerializer.Serialize(writer, document, typeInfo);
            }

            var clone = JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo)
                ?? throw new InvalidOperationException($"Failed to clone document of type {typeof(T).Name}.");
            if (buffer.Capacity > 1024 * 1024)
            {
                t_buffer = null; // do not keep an unusually large buffer alive
            }

            return clone;
        };
    }

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_buffer;

    /// <summary>
    /// Returns a trim/AOT-safe fingerprint function (the document's JSON text) for detecting an operation
    /// id reused with a different payload.
    /// </summary>
    public static Func<T, string> JsonFingerprint<T>(JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return document => JsonSerializer.Serialize(document, typeInfo);
    }
}
