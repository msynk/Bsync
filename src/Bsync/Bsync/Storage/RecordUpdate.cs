namespace Bsync.Storage;

/// <summary>A conditional transformation of one record, applied by <see cref="ILocalStore{TDocument}.UpdateAsync"/>.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
/// <param name="Id">The record id. The returned record's <c>Current.Id</c> must equal it.</param>
/// <param name="Transform">Maps the committed state to the new state, or to <see langword="null"/> for no change.</param>
public sealed record RecordUpdate<TDocument>(string Id, Func<SyncRecord<TDocument>?, SyncRecord<TDocument>?> Transform)
    where TDocument : class, ISyncEntity
{
    /// <summary>
    /// Set by the engine for transforms that never change the record or documents they receive: the in-memory store then
    /// passes its own record instead of a copy (D9).
    /// </summary>
    internal bool Pure { get; init; }

    /// <summary>Set by the engine when it does not read the resulting record: the in-memory store then returns none (D9).</summary>
    internal bool ResultUnused { get; init; }

    /// <summary>
    /// Set by the engine when the documents its transform adds are referenced nowhere else (a server's answer): the
    /// in-memory store then keeps them instead of copying them (D9).
    /// </summary>
    internal bool Adopt { get; init; }
}
