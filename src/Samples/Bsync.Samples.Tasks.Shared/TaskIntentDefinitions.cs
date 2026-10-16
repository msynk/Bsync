using System.Globalization;
using System.Text.Json.Serialization;
using Bsync.Client;

namespace Bsync.Samples.Tasks;

/// <summary>Explicit payload registrations shared by clients and the server; no runtime type-name dispatch.</summary>
public static class TaskIntentDefinitions
{
    /// <summary>Complete a work item.</summary>
    public static IntentDefinition<CompleteTaskPayload> Complete { get; } = new(IntentKinds.Complete, 1, TaskIntentPayloadJson.Default.CompleteTaskPayload);
    /// <summary>Rename a work item.</summary>
    public static IntentDefinition<RenameTaskPayload> Rename { get; } = new(IntentKinds.Rename, 1, TaskIntentPayloadJson.Default.RenameTaskPayload);

    /// <summary>
    /// Captures the existing sample document as an immutable request without changing its wire shape or ledger hash.
    /// Unknown definitions fail before decoding. The server separately overwrites all submitted execution fields.
    /// </summary>
    public static IntentRequest Capture(TaskIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.RequestSchema != 1) throw new InvalidOperationException("Unsupported request schema.");
        var revision = intent.TaskRevision?.ToString(CultureInfo.InvariantCulture);
        return intent.Kind switch
        {
            IntentKinds.Complete => Complete.Create(intent.Id, intent.TaskId, revision, new()),
            IntentKinds.Rename => Rename.Create(intent.Id, intent.TaskId, revision, new(intent.Title)),
            _ => throw new InvalidOperationException("Unsupported intent kind."),
        };
    }
}

/// <summary>Completion has no extra parameters beyond target and expected revision.</summary>
public sealed record CompleteTaskPayload;
/// <summary>Rename request payload; title validity remains a server domain rule.</summary>
public sealed record RenameTaskPayload(string? Title);

[JsonSerializable(typeof(CompleteTaskPayload))]
[JsonSerializable(typeof(RenameTaskPayload))]
internal partial class TaskIntentPayloadJson : JsonSerializerContext;
