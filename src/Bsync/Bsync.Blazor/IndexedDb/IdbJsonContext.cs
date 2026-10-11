using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bsync.Clocks;
using Microsoft.JSInterop;

namespace Bsync.Blazor.IndexedDb;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<IdbCommitEntry>))]
[JsonSerializable(typeof(IdbMetaUpdate))]
[JsonSerializable(typeof(IdbRead))]
[JsonSerializable(typeof(IdbMeta))]
[JsonSerializable(typeof(List<IdbRecord>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<IdbLiveRecord>))]
[JsonSerializable(typeof(List<IdbIndexKeys>))]
internal sealed partial class IdbJsonContext : JsonSerializerContext;
