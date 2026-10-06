using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Protocol;

namespace Bsync.Transport;

/// <summary>
/// Combines the pulls of several collections of one account into one request (task C3, feature <c>pull-batch</c>,
/// protocol §8.5). Share one instance between the <see cref="HttpSyncTransport{TDocument}"/>s of an account's collections
/// (<see cref="HttpSyncTransportOptions.PullBatch"/>): pulls that start within <see cref="Window"/> of each other go in one
/// request. A transport uses it only after its server advertised the feature; until then, and against older servers, each
/// collection pulls on its own.
/// </summary>
public sealed class HttpPullBatch
{
    private readonly HttpClient _http;
    private readonly string _path;
    private readonly string _schemaId;
    private readonly Lock _gate = new();
    private List<Pending>? _queue;

    /// <summary>Creates the batcher for one server group.</summary>
    /// <param name="http">The account's client (credentials as for the collections).</param>
    /// <param name="schemaId">The application's schema id.</param>
    /// <param name="basePath">The group's prefix (<c>MapSyncCollections</c>'s <c>prefix</c>). Default <c>sync</c>.</param>
    public HttpPullBatch(HttpClient http, string schemaId, string basePath = "sync")
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentNullException.ThrowIfNull(basePath);
        _http = http;
        _schemaId = schemaId;
        _path = $"{basePath.Trim('/')}/pull";
    }

    /// <summary>How long a pull waits for others to join its request. Default 10 ms.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>The timeout of one batch request. Default 60 seconds.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The number of batch requests sent (diagnostics, tests).</summary>
    public int Requests => Volatile.Read(ref _requests);

    private int _requests;

    /// <summary>
    /// Queues one collection's pull and returns its part of the answer: the status and the JSON body (a pull result,
    /// or a problem). Throws <see cref="SyncTransportException"/> when the batch request itself fails.
    /// </summary>
    internal async Task<(int Status, JsonElement Body)> PullAsync(string collection, PullRequest request, JsonTypeInfo<PullRequest> requestType, CancellationToken cancellationToken)
    {
        var pending = new Pending(collection, JsonSerializer.SerializeToElement(request, requestType));
        Enqueue(pending);
        return await pending.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Enqueue(Pending pending)
    {
        bool first;
        lock (_gate)
        {
            first = _queue is null;
            _queue ??= [];
            _queue.Add(pending);
        }

        if (first)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(Window).ConfigureAwait(false);
                List<Pending> batch;
                lock (_gate)
                {
                    batch = _queue!;
                    _queue = null;
                }

                await SendAsync(batch).ConfigureAwait(false);
            }, CancellationToken.None);
        }
    }

    private async Task SendAsync(List<Pending> batch)
    {
        // One part per collection name: a collection asking twice in one window gets its second pull in the next batch.
        var parts = batch.GroupBy(p => p.Collection, StringComparer.Ordinal).Select(g => g.First()).ToList();
        foreach (var later in batch.Except(parts))
        {
            Enqueue(later);
        }

        Interlocked.Increment(ref _requests);
        try
        {
            using var body = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(body))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("collections");
                foreach (var part in parts)
                {
                    writer.WritePropertyName(part.Collection);
                    part.Request.WriteTo(writer);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var message = new HttpRequestMessage(HttpMethod.Post, _path) { Content = new ByteArrayContent(body.ToArray()) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            message.Headers.Add("Bsync-Protocol", HttpProblems.ProtocolVersion);
            message.Headers.Add("Bsync-Schema", _schemaId);
            using var response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                    ? new NotSupportedException("The server does not offer pull batches.")
                    : await HttpProblems.ToExceptionAsync(response, CancellationToken.None).ConfigureAwait(false);
                foreach (var part in parts)
                {
                    part.Done.TrySetException(failure);
                }

                return;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            var results = document.RootElement.GetProperty("results");
            foreach (var part in parts)
            {
                if (results.TryGetProperty(part.Collection, out var answer)
                    && answer.TryGetProperty("status", out var status) && status.TryGetInt32(out var code)
                    && (answer.TryGetProperty("result", out var payload) || answer.TryGetProperty("problem", out payload)))
                {
                    part.Done.TrySetResult((code, payload.Clone()));
                }
                else
                {
                    part.Done.TrySetException(new SyncProtocolException($"The pull batch has no valid answer for '{part.Collection}'."));
                }
            }
        }
        catch (Exception error)
        {
            Exception failure = error switch
            {
                HttpRequestException or OperationCanceledException => new SyncTransportException(SyncErrorCodes.Unavailable, "The server could not be reached.", isTransient: true, innerException: error),
                JsonException or KeyNotFoundException or InvalidOperationException => new SyncProtocolException("The server returned a malformed pull batch.", error),
                _ => error,
            };
            foreach (var part in parts)
            {
                part.Done.TrySetException(failure);
            }
        }
    }

    private sealed record Pending(string Collection, JsonElement Request)
    {
        public TaskCompletionSource<(int Status, JsonElement Body)> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
