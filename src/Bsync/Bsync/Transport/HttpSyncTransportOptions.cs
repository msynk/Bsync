using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Protocol;

namespace Bsync.Transport;

/// <summary>Options for <see cref="HttpSyncTransport{TDocument}"/>.</summary>
public sealed class HttpSyncTransportOptions
{
    /// <summary>The collection name used in the endpoint path.</summary>
    public required string Collection { get; init; }

    /// <summary>
    /// The application's schema identifier for this collection, sent as <c>Bsync-Schema</c>. The server
    /// refuses requests from schemas it does not support (<c>upgrade-required</c>).
    /// </summary>
    public required string SchemaId { get; init; }

    /// <summary>Path prefix of the endpoints, relative to <see cref="HttpClient.BaseAddress"/>. Default <c>sync</c>.</summary>
    public string BasePath { get; init; } = "sync";

    /// <summary>Per-request timeout. A timed-out push has an unknown outcome and is retried. Default 30 seconds.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shared by the transports of one account's collections in a server group: their pulls go in one request when the
    /// server offers <c>pull-batch</c> (task C3). <see langword="null"/>: each collection pulls on its own.
    /// </summary>
    public HttpPullBatch? PullBatch { get; init; }
}
