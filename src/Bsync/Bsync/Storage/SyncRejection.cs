namespace Bsync.Storage;

/// <summary>A permanent server rejection of one local revision.</summary>
/// <param name="Revision">The local revision that was rejected.</param>
/// <param name="ErrorCode">The server's machine-readable reason.</param>
/// <param name="Message">The server's explanation, if any.</param>
public sealed record SyncRejection(long Revision, string ErrorCode, string? Message)
{
    /// <summary>
    /// Values that complete <see cref="ErrorCode"/> for the UI, for example <c>{ "max": "200" }</c> for a too-long title
    /// (task C5). Stable keys chosen by the server; values are text.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Arguments { get; init; }

    /// <inheritdoc />
    public bool Equals(SyncRejection? other) =>
        other is not null && Revision == other.Revision && ErrorCode == other.ErrorCode && Message == other.Message
        && SameArguments(Arguments, other.Arguments);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Revision, ErrorCode, Message, Arguments?.Count ?? -1);

    internal static bool SameArguments(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b) =>
        (a is null || a.Count == 0) && (b is null || b.Count == 0)
        || (a is not null && b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var value) && value == kv.Value));
}
