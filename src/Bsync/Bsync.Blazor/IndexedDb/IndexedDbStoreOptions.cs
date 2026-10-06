using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Bsync.Clocks;
using Microsoft.JSInterop;

namespace Bsync.Blazor.IndexedDb;

/// <summary>Options for <see cref="IndexedDbLocalStore{TDocument}"/>.</summary>
public sealed class IndexedDbStoreOptions
{
    /// <summary>
    /// IndexedDB database name. Include the signed-in account (for example <c>bsync-{userId}</c>) so
    /// different accounts on one browser profile never share a replica.
    /// </summary>
    public required string DatabaseName { get; init; }

    /// <summary>Collection name within the database. Default <c>default</c>.</summary>
    public string Collection { get; init; } = "default";

    /// <summary>How long opening waits for other tabs that block a schema upgrade. Default 10 seconds.</summary>
    public TimeSpan BlockedTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum optimistic commit attempts when another tab writes the same records concurrently. Default 20.</summary>
    public int MaxCommitAttempts { get; init; } = 20;

    /// <summary>
    /// A 32-byte key: documents are encrypted with AES-GCM (WebCrypto) before they are stored (ADR-016). The key is kept in
    /// memory only; supply it from somewhere an attacker with the browser profile does not have (for example fetched from
    /// your server after sign-in). Ids, timestamps, flags and declared index values stay readable. Opening an encrypted
    /// database without the key, or with another one, fails with <see cref="Storage.LocalStoreUnavailableException"/>
    /// (reason <c>key</c>). <see langword="null"/>: not encrypted.
    /// </summary>
    public byte[]? EncryptionKey { get; init; }
}
