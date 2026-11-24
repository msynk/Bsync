using System.Text.Json;

namespace Bsync.Documents;

/// <summary>
/// Helpers for upgrading documents written by older versions of an application (ADR-011, docs/guide.md#disaster-recovery-and-stuck-replicas
/// §7). Bsync stores and transmits documents as JSON and reads them with the application's current contract, so the
/// upgrade belongs in the document type itself, where every reader uses it: in the local stores, on the wire and on the
/// server.
/// </summary>
/// <remarks>
/// <para>Pattern: keep a <c>[JsonExtensionData]</c> bag, implement <see cref="System.Text.Json.Serialization.IJsonOnDeserialized"/>,
/// and move old members into their new place.</para>
/// <code>
/// public sealed class Note : ISyncEntity, IJsonOnDeserialized
/// {
///     public string Heading { get; set; } = "";          // was "title" in version 1
///     [JsonExtensionData] public Dictionary&lt;string, JsonElement&gt;? Unknown { get; set; }
///
///     void IJsonOnDeserialized.OnDeserialized()
///     {
///         if (DocumentUpgrade.TryTake(Unknown, "title", out var title) &amp;&amp; Heading.Length == 0)
///             Heading = title.GetString() ?? "";
///     }
/// }
/// </code>
/// <para>Taking the old member out of the bag means the upgraded document no longer carries it: the next write stores
/// and sends only the new shape. Members this version does not know stay in the bag and survive edits.</para>
/// </remarks>
public static class DocumentUpgrade
{
    /// <summary>Removes the member <paramref name="name"/> from <paramref name="unknown"/> and returns it, if present.</summary>
    public static bool TryTake(Dictionary<string, JsonElement>? unknown, string name, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (unknown is not null && unknown.Remove(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
