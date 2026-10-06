using System.Net;
using System.Runtime.CompilerServices;

namespace Bsync.Transport;

/// <summary>Client side of the multiplexed hint stream (feature <c>hints-multiplex</c>, task C3).</summary>
public static class HttpSyncHints
{
    /// <summary>
    /// For <see cref="Client.SyncCoordinatorOptions.Hints"/>: one Server-Sent Events stream (<c>GET {basePath}/hints</c>) per
    /// account for all coordinated collections of a <c>MapSyncCollections</c> group, yielding a collection's name whenever
    /// it changed (and every name once on connect). Throws <see cref="NotSupportedException"/> when the server has no such
    /// stream, so sessions fall back to one stream each.
    /// </summary>
    /// <param name="client">The HTTP client for an account (with its credentials and base address).</param>
    /// <param name="schemaId">The application schema id the server accepts (<c>Bsync-Schema</c>).</param>
    /// <param name="basePath">The group's path prefix. Default <c>sync</c>.</param>
    public static Func<string, IReadOnlyList<string>, CancellationToken, IAsyncEnumerable<string>> Multiplexed(
        Func<string, HttpClient> client,
        string schemaId,
        string basePath = "sync")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(schemaId);
        ArgumentNullException.ThrowIfNull(basePath);
        return (account, collections, cancellationToken) => StreamAsync(client(account), schemaId, basePath.Trim('/'), collections, cancellationToken);
    }

    private static async IAsyncEnumerable<string> StreamAsync(
        HttpClient http,
        string schemaId,
        string basePath,
        IReadOnlyList<string> collections,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = string.Join(',', collections.Select(Uri.EscapeDataString));
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{basePath}/hints?collections={query}");
        message.Headers.Add("Bsync-Protocol", "1");
        message.Headers.Add("Bsync-Schema", schemaId);
        message.Headers.Accept.ParseAdd("text/event-stream");
        message.Options.Set(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new SyncTransportException(SyncErrorCodes.Unavailable, "The server could not be reached.", isTransient: true, innerException: error);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                throw new NotSupportedException("The server does not offer a multiplexed hint stream.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SyncTransportException(
                    response.StatusCode == HttpStatusCode.Unauthorized ? SyncErrorCodes.Unauthorized : SyncErrorCodes.Unavailable,
                    $"The hint stream answered {(int)response.StatusCode}.",
                    isTransient: (int)response.StatusCode >= 500,
                    statusCode: (int)response.StatusCode);
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            string? data = null;
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException error)
                {
                    throw new SyncTransportException(SyncErrorCodes.Unavailable, "The hint stream was interrupted.", isTransient: true, innerException: error);
                }

                if (line is null)
                {
                    yield break;
                }

                if (line.Length == 0)
                {
                    if (data is not null)
                    {
                        yield return data;
                        data = null;
                    }
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data = line["data:".Length..].Trim();
                }
            }
        }
    }
}
