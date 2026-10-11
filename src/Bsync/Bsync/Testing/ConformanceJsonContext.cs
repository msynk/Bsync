using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Testing;

/// <summary>Source-generated JSON metadata for <see cref="ConformanceDocument"/> and its protocol messages.</summary>
[JsonSerializable(typeof(ConformanceDocument))]
[JsonSerializable(typeof(Protocol.PullRequest))]
[JsonSerializable(typeof(Protocol.PullResult<ConformanceDocument>))]
[JsonSerializable(typeof(Protocol.PushRequest<ConformanceDocument>))]
[JsonSerializable(typeof(Protocol.PushResult<ConformanceDocument>))]
public sealed partial class ConformanceJsonContext : JsonSerializerContext;
