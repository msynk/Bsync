using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bsync.Server.AspNetCore;

/// <summary>Maps the Bsync HTTP binding (docs/protocol/v1.md §8).</summary>
/// <remarks>
/// <para>
/// Endpoints: <c>POST {prefix}/collections/{collection}/pull</c> and <c>…/push</c>. Every request must carry
/// <c>Bsync-Protocol: 1</c>, a supported <c>Bsync-Schema</c> and <c>Content-Type:
/// application/json</c>. Errors are RFC 9457 problem details with a <c>code</c> member.
/// </para>
/// <para>
/// Authentication and authorization policies are applied by the caller on the returned route group
/// (<c>.RequireAuthorization(...)</c>). With cookie authentication, the required custom header and JSON
/// content type make every call a CORS-preflighted request, so cross-site form posts cannot reach the
/// endpoints; do not relax CORS for untrusted origins. Request and response bodies are never logged.
/// </para>
/// <para>
/// Refused requests are logged (category <c>Bsync.Server</c>) with their code, status, collection and
/// endpoint, and every request is counted in the <see cref="MeterName"/> meter (docs/operations/observability.md).
/// An unexpected exception from the authority is logged as an error and answered with <c>503 unavailable</c>,
/// which clients retry with backoff.
/// </para>
/// </remarks>
public static class SyncEndpoints
{
    /// <summary>The protocol version this server speaks.</summary>
    public const string ProtocolVersion = "1";

    /// <summary>The name of the server-side <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
    public const string MeterName = "Bsync.Server";

    private const string Ok = "ok";

    private static readonly Meter ServerMeter = new(MeterName, typeof(SyncEndpoints).Assembly.GetName().Version?.ToString());

    private static readonly Counter<long> Requests = ServerMeter.CreateCounter<long>(
        "bsync.server.requests", "{request}", "Protocol requests, by collection, endpoint and result (ok or a problem code).");

    private static readonly Counter<long> Operations = ServerMeter.CreateCounter<long>(
        "bsync.server.push.operations", "{operation}", "Push operations decided, by collection and outcome.");

    /// <summary>Maps pull and push endpoints for <paramref name="collection"/> backed by <paramref name="authority"/>.</summary>
    /// <returns>The route group, for adding authorization, CORS or rate limiting.</returns>
    public static RouteGroupBuilder MapSyncCollection<TDocument>(
        this IEndpointRouteBuilder endpoints,
        string collection,
        ISyncAuthority<TDocument> authority,
        SyncJsonTypes<TDocument> json,
        SyncEndpointOptions options,
        string prefix = "sync")
        where TDocument : class, ISyncEntity =>
        MapCollection(endpoints, collection, authority, json, options, prefix, multiplexedHints: false);

    internal static RouteGroupBuilder MapCollection<TDocument>(
        IEndpointRouteBuilder endpoints,
        string collection,
        ISyncAuthority<TDocument> authority,
        SyncJsonTypes<TDocument> json,
        SyncEndpointOptions options,
        string prefix,
        bool multiplexedHints,
        Dictionary<string, BatchPull>? batch = null)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(options);
        if (!SyncIds.IsValid(collection) || collection.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("The collection name must be a valid identifier without '/'.", nameof(collection));
        }

        var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Bsync.Server") ?? NullLogger.Instance;
        Func<SyncCallContext, PullRequest, CancellationToken, Task<PullResult<TDocument>>> pull = multiplexedHints
            ? async (context, request, cancellationToken) =>
            {
                var result = await authority.PullAsync(context, request, cancellationToken).ConfigureAwait(false);
                return result with { Features = [.. result.Features ?? [], SyncFeatures.HintsMultiplex, SyncFeatures.PullBatch] };
            }
            : authority.PullAsync;
        if (options.ReplicaAudit is { } audit)
        {
            var unaudited = pull;
            pull = async (context, request, cancellationToken) =>
            {
                var result = await unaudited(context, request, cancellationToken).ConfigureAwait(false);
                if (!result.HasMore && request.Replica is { Length: > 0 and <= 256 } replica)
                {
                    try
                    {
                        await audit.RecordAsync(
                            new ReplicaAcknowledgement(replica, context.Principal.Identity?.Name, collection, context.Scope, result.Checkpoint.Value ?? string.Empty, options.TimeProvider.GetUtcNow()),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogWarning(error, "Recording a replica acknowledgement for {Collection} failed.", collection);
                    }
                }

                return result;
            };
        }

        Func<SyncCallContext, PushRequest<TDocument>, CancellationToken, Task<PushResult<TDocument>>> push = authority.PushAsync;
        Func<PullRequest, string?> validatePull = static request => request.BatchSize < 1 ? "The pull limit must be at least 1." : null;
        Func<PushRequest<TDocument>, string?> validatePush = request => request.Operations is null
            ? "The push request has no operations."
            : request.Operations.Count > authority.Limits.MaxOperationsPerPush ? TooMany : null;
        Action<PushResult<TDocument>> countOutcomes = result =>
        {
            if (!Operations.Enabled)
            {
                return;
            }

            foreach (var outcome in result.Outcomes)
            {
                Operations.Add(1, new TagList
                {
                    { "bsync.collection", collection },
                    { "bsync.outcome", outcome.Kind.ToString() },
                    { "bsync.duplicate", outcome.IsDuplicate },
                });
            }
        };

        var group = endpoints.MapGroup($"{prefix.Trim('/')}/collections/{collection}");
        var pullSite = new Site(collection, "pull", logger);
        var pushSite = new Site(collection, "push", logger);
        group.MapPost("pull", (RequestDelegate)(context => HandleAsync(context, pullSite, options, json.PullRequest, json.PullResult, pull, validatePull, null)));
        if (batch is not null)
        {
            // The same pull, as one part of a group's batch: same validation, same features and audit, same error codes.
            batch[collection] = async (context, element, cancellationToken) =>
            {
                PullRequest request;
                try
                {
                    request = element.Deserialize(json.PullRequest);
                }
                catch (JsonException)
                {
                    return BatchPart.Problem(pullSite, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, "Not a valid pull request.");
                }

                if (validatePull(request) is { } invalid)
                {
                    return BatchPart.Problem(pullSite, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, invalid);
                }

                try
                {
                    var result = await pull(context, request, cancellationToken).ConfigureAwait(false);
                    pullSite.Count(Ok);
                    return new BatchPart(StatusCodes.Status200OK, writer => JsonSerializer.Serialize(writer, result, json.PullResult));
                }
                catch (SyncResetRequiredException error)
                {
                    return BatchPart.Problem(pullSite, StatusCodes.Status409Conflict, SyncErrorCodes.ResetRequired, "The checkpoint cannot be served; reset and resnapshot.", error.Reason);
                }
                catch (SyncProtocolException error)
                {
                    return BatchPart.Problem(pullSite, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, error.Message);
                }
                catch (SyncTransportException error)
                {
                    return BatchPart.Problem(pullSite, StatusFor(error.ErrorCode), error.ErrorCode, error.Message);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    ServerLog.AuthorityFailed(pullSite.Logger, collection, "pull", error);
                    return BatchPart.Problem(pullSite, StatusCodes.Status503ServiceUnavailable, SyncErrorCodes.Unavailable, "The server could not process the request; try again later.");
                }
            };
        }

        group.MapPost("push", (RequestDelegate)(context => HandleAsync(context, pushSite, options, json.PushRequest, json.PushResult, push, validatePush, countOutcomes)));
        if (authority is ISyncCommitNotifier notifier)
        {
            var hintsSite = new Site(collection, "hints", logger);
            group.MapGet("hints", (RequestDelegate)(context => HintsAsync(context, hintsSite, options, notifier)));
        }

        return group;
    }

    /// <summary>
    /// Maps several collections that share <paramref name="options"/> (scope resolver, schemas, limits), for example
    /// <c>app.MapSyncCollections(options, group => group.Add&lt;Order&gt;("orders").Add&lt;Customer&gt;("customers"))</c>.
    /// </summary>
    /// <returns>The route group of all the collections, for one authorization policy, CORS or rate limiting.</returns>
    public static RouteGroupBuilder MapSyncCollections(
        this IEndpointRouteBuilder endpoints,
        SyncEndpointOptions options,
        Action<SyncCollectionGroupBuilder> collections,
        string prefix = "sync")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(prefix);
        var group = endpoints.MapGroup(prefix.Trim('/'));
        var builder = new SyncCollectionGroupBuilder(group, options);
        collections(builder);
        var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Bsync.Server") ?? NullLogger.Instance;
        var site = new Site("*", "hints", logger);
        var notifiers = builder.Notifiers;
        group.MapGet("hints", (RequestDelegate)(context => MultiplexedHintsAsync(context, site, options, notifiers)));
        var batchSite = new Site("*", "pull-batch", logger);
        var pulls = builder.Pulls;
        group.MapPost("pull", (RequestDelegate)(context => PullBatchAsync(context, batchSite, options, pulls)));
        return group;
    }

    /// <summary>How often an idle hint stream sends a keep-alive comment.</summary>
    public static TimeSpan HintKeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Where a request arrived, for logs and metrics.</summary>
    internal sealed record Site(string Collection, string Endpoint, ILogger Logger)
    {
        public void Count(string result)
        {
            if (Requests.Enabled)
            {
                Requests.Add(1, new TagList
                {
                    { "bsync.collection", Collection },
                    { "bsync.endpoint", Endpoint },
                    { "bsync.result", result },
                });
            }
        }
    }

    /// <summary>
    /// Server-Sent Events: <c>event: resync</c> once on connect (so nothing committed between the client's last pull
    /// and the subscription is missed) and after every commit in the caller's scope. Carries no document ids.
    /// </summary>
    private static async Task HintsAsync(HttpContext http, Site site, SyncEndpointOptions options, ISyncCommitNotifier notifier)
    {
        var scope = await AuthorizeAsync(http, site, options, requireJson: false);
        if (scope is null)
        {
            return;
        }

        site.Count(Ok);
        var signal = System.Threading.Channels.Channel.CreateBounded<bool>(
            new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });
        void OnCommitted(AuthorityCommit commit)
        {
            if (string.Equals(commit.Scope, scope, StringComparison.Ordinal))
            {
                signal.Writer.TryWrite(true);
            }
        }

        notifier.Committed += OnCommitted;
        try
        {
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            signal.Writer.TryWrite(true);

            var aborted = http.RequestAborted;
            while (!aborted.IsCancellationRequested)
            {
                using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                keepAlive.CancelAfter(HintKeepAlive);
                string message;
                try
                {
                    await signal.Reader.ReadAsync(keepAlive.Token);
                    message = "event: resync\ndata: {}\n\n";
                }
                catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                {
                    message = ": keep-alive\n\n";
                }

                await http.Response.WriteAsync(message, aborted);
                await http.Response.Body.FlushAsync(aborted);
            }
        }
        catch (OperationCanceledException)
        {
            // The client disconnected.
        }
        finally
        {
            notifier.Committed -= OnCommitted;
        }
    }

    /// <summary>
    /// One hint stream for several collections of a group (feature <c>hints-multiplex</c>): <c>?collections=a,b</c> names
    /// them; each event's data is the name of a collection that changed in the caller's scope. Every requested collection
    /// is announced once on connect. Unknown names are ignored.
    /// </summary>
    private static async Task MultiplexedHintsAsync(HttpContext http, Site site, SyncEndpointOptions options, IReadOnlyDictionary<string, ISyncCommitNotifier> notifiers)
    {
        var scope = await AuthorizeAsync(http, site, options, requireJson: false);
        if (scope is null)
        {
            return;
        }

        var requested = http.Request.Query["collections"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .Where(notifiers.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        site.Count(Ok);
        var signal = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var handlers = new List<(ISyncCommitNotifier Notifier, Action<AuthorityCommit> Handler)>();
        foreach (var collection in requested)
        {
            Action<AuthorityCommit> handler = commit =>
            {
                if (string.Equals(commit.Scope, scope, StringComparison.Ordinal))
                {
                    signal.Writer.TryWrite(collection);
                }
            };
            notifiers[collection].Committed += handler;
            handlers.Add((notifiers[collection], handler));
        }

        try
        {
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            foreach (var collection in requested)
            {
                signal.Writer.TryWrite(collection);
            }

            var aborted = http.RequestAborted;
            var due = new HashSet<string>(StringComparer.Ordinal);
            while (!aborted.IsCancellationRequested)
            {
                using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                keepAlive.CancelAfter(HintKeepAlive);
                var message = new System.Text.StringBuilder();
                try
                {
                    // Coalesce: one event per collection however many commits arrived meanwhile.
                    due.Add(await signal.Reader.ReadAsync(keepAlive.Token));
                    while (signal.Reader.TryRead(out var more))
                    {
                        due.Add(more);
                    }

                    foreach (var collection in due)
                    {
                        message.Append("event: hint\ndata: ").Append(collection).Append("\n\n");
                    }

                    due.Clear();
                }
                catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                {
                    message.Append(": keep-alive\n\n");
                }

                await http.Response.WriteAsync(message.ToString(), aborted);
                await http.Response.Body.FlushAsync(aborted);
            }
        }
        catch (OperationCanceledException)
        {
            // The client disconnected.
        }
        finally
        {
            foreach (var (notifier, handler) in handlers)
            {
                notifier.Committed -= handler;
            }
        }
    }

    /// <summary>Checks protocol, schema, content type and scope; writes the problem response and returns null if any fails.</summary>
    private static async Task<string?> AuthorizeAsync(HttpContext http, Site site, SyncEndpointOptions options, bool requireJson)
    {
        var headers = http.Request.Headers;
        if (!headers.TryGetValue("Bsync-Protocol", out var protocol) || protocol.Count != 1)
        {
            await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, "The Bsync-Protocol header is required.");
            return null;
        }

        if (protocol[0] != ProtocolVersion)
        {
            await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.UpgradeRequired, $"Protocol version {protocol[0]} is not supported; this server speaks {ProtocolVersion}.");
            return null;
        }

        if (!headers.TryGetValue("Bsync-Schema", out var schema) || schema.Count != 1 || !options.SupportedSchemas.Contains(schema[0]!))
        {
            await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.UpgradeRequired, "The application schema is not supported by this server.");
            return null;
        }

        if (requireJson && !http.Request.HasJsonContentType())
        {
            await ProblemAsync(http, site, StatusCodes.Status415UnsupportedMediaType, SyncErrorCodes.InvalidRequest, "Content-Type must be application/json.");
            return null;
        }

        var scope = options.ResolveScope(http);
        if (scope is null)
        {
            await ProblemAsync(http, site, StatusCodes.Status403Forbidden, SyncErrorCodes.Forbidden, "No scope is available for this caller.");
        }

        return scope;
    }

    private const string TooMany = "too-many-operations";

    private static async Task HandleAsync<TRequest, TResponse>(
        HttpContext http,
        Site site,
        SyncEndpointOptions options,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        Func<SyncCallContext, TRequest, CancellationToken, Task<TResponse>> call,
        Func<TRequest, string?> validate,
        Action<TResponse>? observe)
    {
        var scope = await AuthorizeAsync(http, site, options, requireJson: true);
        if (scope is null)
        {
            return;
        }

        if (http.Request.ContentLength > options.MaxRequestBodyBytes)
        {
            await ProblemAsync(http, site, StatusCodes.Status413PayloadTooLarge, SyncErrorCodes.PayloadTooLarge, "The request body is too large.");
            return;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = options.MaxRequestBodyBytes;
        }

        TRequest? request;
        try
        {
            // Enforced here as well as by the server feature: chunked bodies have no Content-Length and some
            // hosts do not support IHttpMaxRequestBodySizeFeature.
            await using var body = new LimitedReadStream(http.Request.Body, options.MaxRequestBodyBytes);
            request = await JsonSerializer.DeserializeAsync(body, requestType, http.RequestAborted);
        }
        catch (JsonException)
        {
            await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, "The request body is not a valid Bsync message.");
            return;
        }
        catch (LimitedReadStream.LimitExceededException)
        {
            await ProblemAsync(http, site, StatusCodes.Status413PayloadTooLarge, SyncErrorCodes.PayloadTooLarge, "The request body is too large.");
            return;
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await ProblemAsync(http, site, StatusCodes.Status413PayloadTooLarge, SyncErrorCodes.PayloadTooLarge, "The request body is too large.");
            return;
        }

        if (request is null)
        {
            await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, "The request body is empty.");
            return;
        }

        if (validate(request) is { } invalid)
        {
            if (invalid == TooMany)
            {
                await ProblemAsync(http, site, StatusCodes.Status413PayloadTooLarge, SyncErrorCodes.PayloadTooLarge, "The push carries more operations than this server accepts.");
            }
            else
            {
                await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, invalid);
            }

            return;
        }

        TResponse response;
        try
        {
            response = await call(new SyncCallContext(http.User, scope), request, http.RequestAborted);
        }
        catch (SyncResetRequiredException error)
        {
            await ProblemAsync(http, site, StatusCodes.Status409Conflict, SyncErrorCodes.ResetRequired, "The checkpoint cannot be served; reset and resnapshot.", reason: error.Reason);
            return;
        }
        catch (SyncProtocolException error)
        {
            await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, error.Message);
            return;
        }
        catch (SyncTransportException error)
        {
            await ProblemAsync(http, site, StatusFor(error.ErrorCode), error.ErrorCode, error.Message, error.RetryAfter);
            return;
        }
        catch (Exception error) when (!http.RequestAborted.IsCancellationRequested)
        {
            // A database outage or a bug in the authority: nothing was committed that the client can rely on, so answer
            // with a retryable error and keep the details in the server log only.
            ServerLog.AuthorityFailed(site.Logger, site.Collection, site.Endpoint, error);
            await ProblemAsync(http, site, StatusCodes.Status503ServiceUnavailable, SyncErrorCodes.Unavailable, "The server could not process the request; try again later.");
            return;
        }

        observe?.Invoke(response);
        site.Count(Ok);
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(http.Response.Body, response, responseType, http.RequestAborted);
    }

    /// <summary>
    /// <c>POST {prefix}/pull</c> (task C3, protocol §8.5): <c>{ "collections": { "name": pullRequest, ... } }</c> answered by
    /// <c>{ "results": { "name": { "status": 200, "result": pullResult } | { "status": 409, "problem": {...} } } }</c>.
    /// Each collection is pulled exactly as its own endpoint would, concurrently; one collection's failure does not
    /// affect the others.
    /// </summary>
    private static async Task PullBatchAsync(HttpContext http, Site site, SyncEndpointOptions options, IReadOnlyDictionary<string, BatchPull> pulls)
    {
        var scope = await AuthorizeAsync(http, site, options, requireJson: true);
        if (scope is null)
        {
            return;
        }

        JsonDocument document;
        try
        {
            await using var body = new LimitedReadStream(http.Request.Body, options.MaxRequestBodyBytes);
            document = await JsonDocument.ParseAsync(body, cancellationToken: http.RequestAborted);
        }
        catch (Exception error) when (error is JsonException or LimitedReadStream.LimitExceededException)
        {
            await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, "The request body is not a valid pull batch.");
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("collections", out var requested) || requested.ValueKind != JsonValueKind.Object)
            {
                await ProblemAsync(http, site, StatusCodes.Status400BadRequest, SyncErrorCodes.InvalidRequest, "The pull batch names no collections.");
                return;
            }

            var context = new SyncCallContext(http.User, scope);
            var parts = requested.EnumerateObject()
                .Select(entry => (entry.Name, Part: pulls.TryGetValue(entry.Name, out var pull)
                    ? pull(context, entry.Value, http.RequestAborted)
                    : Task.FromResult(BatchPart.Problem(site, StatusCodes.Status404NotFound, SyncErrorCodes.InvalidRequest, "No such collection in this group."))))
                .ToList();
            await Task.WhenAll(parts.Select(p => p.Part));

            site.Count(Ok);
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.ContentType = "application/json; charset=utf-8";
            // Written to memory first: serializing a part flushes its writer, and response bodies allow only asynchronous writes.
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            await using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            writer.WriteStartObject("results");
            foreach (var (name, part) in parts)
            {
                var result = await part;
                writer.WriteStartObject(name);
                writer.WriteNumber("status", result.Status);
                writer.WritePropertyName(result.Status == StatusCodes.Status200OK ? "result" : "problem");
                result.Write(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            await writer.FlushAsync(http.RequestAborted);
            await http.Response.Body.WriteAsync(buffer.WrittenMemory, http.RequestAborted);
        }
    }

    /// <summary>One collection's pull within a batch.</summary>
    internal delegate Task<BatchPart> BatchPull(SyncCallContext context, JsonElement request, CancellationToken cancellationToken);

    /// <summary>One collection's answer within a batch: a status and its body (a pull result or a problem).</summary>
    internal sealed record BatchPart(int Status, Action<Utf8JsonWriter> Write)
    {
        public static BatchPart Problem(Site site, int status, string code, string detail, string? reason = null)
        {
            site.Count(code);
            ServerLog.Refused(site.Logger, status >= 500 ? LogLevel.Warning : LogLevel.Information, site.Collection, site.Endpoint, status, code, reason);
            return new(status, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "about:blank");
                writer.WriteNumber("status", status);
                writer.WriteString("detail", detail);
                writer.WriteString("code", code);
                if (reason is not null)
                {
                    writer.WriteString("reason", reason);
                }

                writer.WriteEndObject();
            });
        }
    }

    private static int StatusFor(string code) => code switch
    {
        SyncErrorCodes.ResetRequired => StatusCodes.Status409Conflict,
        SyncErrorCodes.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        SyncErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
        SyncErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        SyncErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        SyncErrorCodes.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    private static Task ProblemAsync(HttpContext http, Site site, int status, string code, string detail, TimeSpan? retryAfter = null, string? reason = null)
    {
        site.Count(code);
        ServerLog.Refused(site.Logger, status >= 500 ? LogLevel.Warning : LogLevel.Information, site.Collection, site.Endpoint, status, code, reason);
        if (retryAfter is { } delay)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(delay.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        http.Response.Headers.CacheControl = "no-store";
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (reason is not null)
        {
            extensions["reason"] = reason;
        }

        return Results.Problem(detail: detail, statusCode: status, extensions: extensions)
            .ExecuteAsync(http);
    }
}
