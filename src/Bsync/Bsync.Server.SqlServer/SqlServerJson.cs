using System.Text.Json.Serialization;

namespace Bsync.Server.SqlServer;

/// <summary>Source-generated JSON for values the authority stores itself (trim/AOT safe).</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SqlServerJson : JsonSerializerContext;
