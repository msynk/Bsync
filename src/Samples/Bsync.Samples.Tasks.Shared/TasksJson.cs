using System.Text.Json.Serialization;
using Bsync.Protocol;

namespace Bsync.Samples.Tasks;

/// <summary>Source-generated JSON for tasks and the protocol messages (trim/AOT safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TaskDocument))]
[JsonSerializable(typeof(PullRequest))]
[JsonSerializable(typeof(PullResult<TaskDocument>))]
[JsonSerializable(typeof(PushRequest<TaskDocument>))]
[JsonSerializable(typeof(PushResult<TaskDocument>))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(NewTask))]
public sealed partial class TasksJson : JsonSerializerContext
{
    /// <summary>The collection name.</summary>
    public const string Collection = "tasks";

    /// <summary>The application schema id sent with every sync request.</summary>
    public const string SchemaId = "tasks-v1";
}

/// <summary>Development sign-in: asks the sample server for a bearer token.</summary>
public sealed record TokenRequest(string User, string Tenant);

/// <summary>A bearer token issued by the sample server.</summary>
public sealed record TokenResponse(string Token);

/// <summary>A task created by the back office through the ordinary API (not by a replica).</summary>
public sealed record NewTask(string Id, string Title);
