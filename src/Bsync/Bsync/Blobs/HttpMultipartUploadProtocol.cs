using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bsync.Blobs;

/// <summary>Authenticated schema-1 multipart control requests; use a separate credential-free client for part PUTs.</summary>
public sealed class HttpMultipartUploadProtocol : IMultipartUploadProtocol
{
    private readonly HttpClient _client;
    /// <inheritdoc />
    public Uri Endpoint { get; }
    /// <summary>Endpoint is the absolute multipart route prefix, without query/fragment/credentials, scoped to the current account.</summary>
    public HttpMultipartUploadProtocol(HttpClient client, Uri endpoint)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("https" or "http") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Expected an absolute authority endpoint.", nameof(endpoint));
        Endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
    }
    private static string Segment(string value) => Uri.EscapeDataString(value);
    private async Task<T?> Call<T>(string path, JsonTypeInfo<T> type, bool negotiate, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, path));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (negotiate && response.StatusCode == HttpStatusCode.NotImplemented) return default;
        // A 404 may mean revoked scope/session. Only an explicit 501 negotiates legacy fallback.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Multipart authority refused the operation.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > 1048576) throw new InvalidDataException("Multipart reply exceeds limits.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > 1048576) throw new InvalidDataException("Multipart reply exceeds limits.");
            bytes.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), type) ?? throw new InvalidDataException("Missing multipart reply.");
    }
    /// <inheritdoc />
    public Task<MultipartUploadSession?> StartAsync(string operationId, string sha256, long size, CancellationToken cancellationToken) =>
        Call($"{Segment(sha256)}/start/{Segment(operationId)}?size={size.ToString(System.Globalization.CultureInfo.InvariantCulture)}", MultipartJson.Default.MultipartUploadSession, true, cancellationToken);
    /// <inheritdoc />
    public async Task<MultipartPartTicket> AuthorizePartAsync(string sessionId, string sha256, int number, CancellationToken cancellationToken) =>
        await Call($"{Segment(sha256)}/{Segment(sessionId)}/part/{number}", MultipartJson.Default.MultipartPartTicket, false, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing part ticket.");
    /// <inheritdoc />
    public async Task<MultipartUploadSession> CompleteAsync(string sessionId, string sha256, CancellationToken cancellationToken) =>
        await Call($"{Segment(sha256)}/{Segment(sessionId)}/complete", MultipartJson.Default.MultipartUploadSession, false, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Missing completion reply.");
    /// <inheritdoc />
    public async Task AbortAsync(string sessionId, string sha256, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync(new Uri(Endpoint, $"{Segment(sha256)}/{Segment(sessionId)}/abort"), null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Multipart abort refused; local state retained.", null, response.StatusCode);
    }
}
