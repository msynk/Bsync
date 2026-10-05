using System.Data.Common;
using Bsync.Protocol;

namespace Bsync.Server;

/// <summary>One write offered to an <see cref="ISyncWriteHandler{TDocument}"/>.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class SyncWriteContext<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>Creates the context.</summary>
    public SyncWriteContext(SyncCallContext caller, PushOperation<TDocument> operation, StoredDocument<TDocument>? current, DbConnection? connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(operation);
        Caller = caller;
        Operation = operation;
        Current = current;
        Connection = connection;
        Transaction = transaction;
    }

    /// <summary>Who is writing, in which scope. Use it, never identities inside the document.</summary>
    public SyncCallContext Caller { get; }

    /// <summary>The operation: its id, the submitted document and the base version.</summary>
    public PushOperation<TDocument> Operation { get; }

    /// <summary>The submitted document (a copy; the handler may change and return it).</summary>
    public TDocument Submitted => Operation.Document;

    /// <summary>The version the replica based its write on, or <see langword="null"/> for a new document.</summary>
    public long? BaseVersion => Operation.BaseVersion;

    /// <summary>The currently stored document and version, if any (tombstones included).</summary>
    public StoredDocument<TDocument>? Current { get; }

    /// <summary>The authority's open connection, or <see langword="null"/> for an authority without a database.</summary>
    public DbConnection? Connection { get; }

    /// <summary>The authority's transaction (enlist every command in it), or <see langword="null"/> without a database.</summary>
    public DbTransaction? Transaction { get; }
}
