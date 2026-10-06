using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Clocks;

namespace Bsync.Samples.Tasks;

/// <summary>
/// A request to act on a task (task F3: an immutable intent). A client creates one with a new id and never changes it, so
/// intents never coalesce; the server executes each exactly once, in the same transaction that accepts it. Accepting an
/// intent is not executing it: <see cref="State"/> says what the server did, and only the server sets it.
/// </summary>
public sealed class TaskIntent : ISyncEntity
{
    /// <inheritdoc />
    public string Id { get; set; } = Guid.CreateVersion7().ToString();

    /// <inheritdoc />
    public HlcTimestamp UpdatedAt { get; set; }

    /// <inheritdoc />
    public bool Deleted { get; set; }

    /// <summary>What to do: <see cref="IntentKinds.Complete"/> or <see cref="IntentKinds.Rename"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The task to act on.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// The task revision the user saw (<see cref="TaskDocument.Revision"/>). If the task changed since, the intent is
    /// rejected with <see cref="IntentCodes.TargetChanged"/> instead of acting on something the user did not see.
    /// <see langword="null"/> acts on whatever the current revision is.
    /// </summary>
    public long? TaskRevision { get; set; }

    /// <summary>The new title, for <see cref="IntentKinds.Rename"/>.</summary>
    public string? Title { get; set; }

    /// <summary>When the user asked (device time; informational).</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set by the server: <see cref="IntentStates.Pending"/>, <see cref="IntentStates.Executed"/> or <see cref="IntentStates.Rejected"/>.</summary>
    public string State { get; set; } = IntentStates.Pending;

    /// <summary>Set by the server when <see cref="State"/> is <see cref="IntentStates.Rejected"/>: a stable code.</summary>
    public string? Code { get; set; }

    /// <summary>Set by the server: who executed it (the authenticated caller, never a value from the client).</summary>
    public string? ExecutedBy { get; set; }

    /// <summary>Members added by newer versions of the app, kept so this version never erases them (I17).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Intent kinds.</summary>
public static class IntentKinds
{
    /// <summary>Marks the task done.</summary>
    public const string Complete = "complete";

    /// <summary>Changes the task's title to <see cref="TaskIntent.Title"/>.</summary>
    public const string Rename = "rename";
}

/// <summary>Execution states. Separate from sync states: an intent can be synced and rejected.</summary>
public static class IntentStates
{
    /// <summary>Not executed yet (not uploaded, or waiting for its task to reach the server).</summary>
    public const string Pending = "pending";

    /// <summary>Executed once.</summary>
    public const string Executed = "executed";

    /// <summary>Not executed; <see cref="TaskIntent.Code"/> says why. Final.</summary>
    public const string Rejected = "rejected";
}

/// <summary>Stable codes of rejected intents. Clients match on them, never on messages.</summary>
public static class IntentCodes
{
    /// <summary>The task changed after the user saw it.</summary>
    public const string TargetChanged = "target-changed";

    /// <summary>The task was deleted.</summary>
    public const string TargetDeleted = "target-deleted";

    /// <summary>The kind is not known to this server.</summary>
    public const string UnknownKind = "unknown-kind";

    /// <summary>An intent was changed after it was created (a sync-level rejection: intents are immutable).</summary>
    public const string Immutable = "intent-immutable";
}
