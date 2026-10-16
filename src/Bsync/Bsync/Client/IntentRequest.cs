using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Bsync.Client;

/// <summary>
/// An immutable action request to embed in an ordinary replicated document. Save once with a stable command ID;
/// persistence and delivery remain the replica's responsibility. This type neither executes nor deduplicates effects.
/// </summary>
public sealed class IntentRequest
{
    /// <summary>Constructs a bounded request, taking a defensive copy of prerequisite identities and validating JSON.</summary>
    [JsonConstructor]
    public IntentRequest(string commandId, string kind, int schemaVersion, string targetId, string? expectedRevision,
        string payloadJson, IReadOnlyList<string>? prerequisites = null)
    {
        ValidateIdentity(commandId, nameof(commandId));
        ValidateKind(kind);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ValidateIdentity(targetId, nameof(targetId));
        if (expectedRevision is not null) ValidateIdentity(expectedRevision, nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(payloadJson);
        if (Encoding.UTF8.GetByteCount(payloadJson) > 65536) throw new ArgumentException("Intent payload exceeds 64 KiB.", nameof(payloadJson));
        using var parsed = JsonDocument.Parse(payloadJson, new() { MaxDepth = 32 });
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Intent payload must be a JSON object.", nameof(payloadJson));
        if (prerequisites?.Count > 64) throw new ArgumentException("At most 64 prerequisite references are supported.", nameof(prerequisites));
        var copied = prerequisites?.ToArray() ?? [];
        foreach (var prerequisite in copied) ValidateIdentity(prerequisite, nameof(prerequisites));
        if (copied.Distinct(StringComparer.Ordinal).Count() != copied.Length)
            throw new ArgumentException("Prerequisite references must be distinct.", nameof(prerequisites));
        CommandId = commandId; Kind = kind; SchemaVersion = schemaVersion; TargetId = targetId;
        ExpectedRevision = expectedRevision; PayloadJson = payloadJson; Prerequisites = new ReadOnlyCollection<string>(copied);
    }

    /// <summary>Stable, application-generated identity; never reuse for a different action.</summary>
    public string CommandId { get; }
    /// <summary>Explicit registered action kind, not a CLR type name.</summary>
    public string Kind { get; }
    /// <summary>Payload contract version, independent of protocol or store schema versions.</summary>
    public int SchemaVersion { get; }
    /// <summary>Application target identity. It does not grant access.</summary>
    public string TargetId { get; }
    /// <summary>Revision seen by the caller, or null for an explicitly unconditional request.</summary>
    public string? ExpectedRevision { get; }
    /// <summary>Immutable JSON object snapshot, limited to 64 KiB UTF-8 and depth 32.</summary>
    public string PayloadJson { get; }
    /// <summary>Application-defined dependency references; they do not imply atomicity or authorization.</summary>
    public IReadOnlyList<string> Prerequisites { get; }

    internal static void ValidateIdentity(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("Identity must be at most 256 characters without controls.", parameter);
        // Reject unpaired surrogates rather than allowing different identities to collapse during UTF-8 encoding.
        _ = new UTF8Encoding(false, true).GetByteCount(value);
    }

    internal static void ValidateKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (kind.Length > 64 || kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("Kind must contain at most 64 ASCII letters, digits, dots, dashes or underscores.", nameof(kind));
    }
}

/// <summary>
/// Explicit registration of one kind/version and source-generated payload metadata. Dispatch on known registrations;
/// never resolve incoming kind strings into CLR types. Keep older registrations while their commands remain replayable.
/// </summary>
public sealed class IntentDefinition<TPayload> where TPayload : class
{
    private readonly JsonTypeInfo<TPayload> _payloadType;
    /// <summary>Registers one payload version. Supply metadata from a source-generated JsonSerializerContext.</summary>
    public IntentDefinition(string kind, int schemaVersion, JsonTypeInfo<TPayload> payloadType)
    {
        IntentRequest.ValidateKind(kind);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        _payloadType = payloadType ?? throw new ArgumentNullException(nameof(payloadType));
        Kind = kind; SchemaVersion = schemaVersion;
    }
    /// <summary>Registered kind.</summary>
    public string Kind { get; }
    /// <summary>Registered payload version.</summary>
    public int SchemaVersion { get; }
    /// <summary>Snapshots mutable payloads immediately. Subsequent payload mutations cannot change this request.</summary>
    public IntentRequest Create(string commandId, string targetId, string? expectedRevision, TPayload payload, IReadOnlyList<string>? prerequisites = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new(commandId, Kind, SchemaVersion, targetId, expectedRevision, JsonSerializer.Serialize(payload, _payloadType), prerequisites);
    }
    /// <summary>Checks both kind and version; unknown registrations must be rejected without execution.</summary>
    public bool Matches(IntentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind == Kind && request.SchemaVersion == SchemaVersion;
    }
    /// <summary>Decodes only this registration. Domain permissions, prerequisites and preconditions must still be revalidated.</summary>
    public TPayload ReadPayload(IntentRequest request)
    {
        if (!Matches(request)) throw new InvalidOperationException("Unknown intent kind or schema version for this registration.");
        return JsonSerializer.Deserialize(request.PayloadJson, _payloadType) ?? throw new JsonException("Intent payload cannot be null.");
    }
}

/// <summary>Server-owned business outcome, independent of transport acceptance.</summary>
public enum IntentExecutionState
{
    /// <summary>Accepted for later dispatch; the effect is not yet confirmed.</summary>
    Queued,
    /// <summary>The server confirmed successful execution.</summary>
    Succeeded,
    /// <summary>A final business refusal, not a transport rejection.</summary>
    Rejected,
    /// <summary>The server confirmed cancellation through a separate cancellation request.</summary>
    Cancelled,
}

/// <summary>
/// Server-produced result to publish separately from the immutable client request. Never trust a submitted result.
/// Missing results mean unknown execution, not failure; cancellation of a client wait cannot recall accepted work.
/// </summary>
/// <param name="CommandId">Command whose outcome this describes.</param>
/// <param name="SchemaVersion">Application result schema, independent of request schema.</param>
/// <param name="State">Confirmed server execution state.</param>
/// <param name="Code">Stable application outcome code, if any.</param>
public sealed record IntentExecutionResult(string CommandId, int SchemaVersion, IntentExecutionState State, string? Code = null);

/// <summary>Independent observations; transport Synced must never be interpreted as business success.</summary>
/// <param name="LocallyPersisted">True only after the ordinary replica save completed durably.</param>
/// <param name="Transport">Ordinary replica status, or null if not available.</param>
/// <param name="Execution">Server-owned execution observation, or null if unknown.</param>
public sealed record IntentStatus(bool LocallyPersisted, SyncItemStatus? Transport, IntentExecutionResult? Execution);
