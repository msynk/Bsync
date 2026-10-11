namespace Bsync.Server.SqlServer;

/// <summary>The database uses a newer Bsync schema than this version understands; nothing was changed.</summary>
/// <param name="message">The message.</param>
public sealed class SqlServerSchemaException(string message) : NotSupportedException(message);
