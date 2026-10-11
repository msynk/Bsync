using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Clocks;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>Source-generated JSON for the store's own metadata columns.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SqliteJson : System.Text.Json.Serialization.JsonSerializerContext;
