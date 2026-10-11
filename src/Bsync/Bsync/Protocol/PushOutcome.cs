using System.Text.Json.Serialization;

namespace Bsync.Protocol;

/// <summary>The server's outcome for one <see cref="PushOperation{TDocument}"/>.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
/// <param name="OperationId">The id of the operation this outcome answers.</param>
/// <param name="Kind">The decision.</param>
public sealed record PushOutcome<TDocument>(
    [property: JsonPropertyName("operationId"), JsonRequired] string OperationId,
    [property: JsonPropertyName("kind"), JsonRequired] PushOutcomeKind Kind)
    where TDocument : class, ISyncEntity
{
    /// <summary>The server version after acceptance, or the current version on conflict.</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(WireNullableInt64JsonConverter))]
    public long? Version { get; init; }

    /// <summary>The authoritative state after acceptance, or the current state on conflict.</summary>
    [JsonPropertyName("document")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TDocument? Document { get; init; }

    /// <summary>A stable machine-readable reason for <see cref="PushOutcomeKind.Rejected"/> or <see cref="PushOutcomeKind.RetryLater"/>.</summary>
    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    /// <summary>A human-readable explanation. Not localized; do not show verbatim to end users.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    /// <summary>
    /// Values that complete <see cref="ErrorCode"/> (task C5), for example the limit a value exceeded. Optional; replicas
    /// that do not know the member ignore it.
    /// </summary>
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Arguments { get; init; }

    /// <summary>
    /// <see langword="true"/> when the server had already decided this operation and is replaying the
    /// stored outcome without applying anything again.
    /// </summary>
    [JsonPropertyName("duplicate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDuplicate { get; init; }

    /// <summary>Creates an accepted outcome.</summary>
    public static PushOutcome<TDocument> Accepted(string operationId, long version, TDocument document) =>
        new(operationId, PushOutcomeKind.Accepted) { Version = version, Document = document };

    /// <summary>Creates a conflict outcome carrying the server's current state.</summary>
    public static PushOutcome<TDocument> Conflict(string operationId, long version, TDocument current) =>
        new(operationId, PushOutcomeKind.Conflict) { Version = version, Document = current };

    /// <summary>Creates a permanent rejection.</summary>
    public static PushOutcome<TDocument> Rejected(string operationId, string errorCode, string? message = null) =>
        new(operationId, PushOutcomeKind.Rejected) { ErrorCode = errorCode, Message = message };

    /// <summary>Creates a retryable outcome.</summary>
    public static PushOutcome<TDocument> RetryLater(string operationId, string errorCode, string? message = null) =>
        new(operationId, PushOutcomeKind.RetryLater) { ErrorCode = errorCode, Message = message };
}
