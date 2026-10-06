using System.Data.Common;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Server;

namespace Bsync.Testing;

/// <summary>
/// The reference driver: one <see cref="InMemorySyncServer{TDocument}"/> per scope, as <see cref="ScopedAuthority{TDocument}"/>
/// does. A new epoch restores every scope's server from its own backup with the version floor.
/// </summary>
public sealed class InMemoryAuthorityDriver : IAuthorityConformanceDriver
{
    /// <inheritdoc />
    public AuthorityCapabilities Capabilities => AuthorityCapabilities.All;

    /// <inheritdoc />
    public Task<AuthorityUnderTest> CreateAsync(AuthorityConformanceOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.FromResult<AuthorityUnderTest>(new InMemoryAuthorityUnderTest(options));
    }

    private sealed class InMemoryAuthorityUnderTest(AuthorityConformanceOptions options) : AuthorityUnderTest
    {
        private static readonly Func<ConformanceDocument, ConformanceDocument> Clone = DocumentCloner.Json(ConformanceJsonContext.Default.ConformanceDocument);
        private static readonly Func<ConformanceDocument, string> Fingerprint = DocumentCloner.JsonFingerprint(ConformanceJsonContext.Default.ConformanceDocument);

        private readonly Dictionary<string, InMemorySyncServer<ConformanceDocument>> _servers = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private Router? _router;

        public override ISyncAuthority<ConformanceDocument> Authority => _router ??= new Router(this);

        public override Task PurgeTombstonesAsync(string scope, long throughVersion, CancellationToken cancellationToken = default)
        {
            For(scope).PurgeTombstones(throughVersion);
            return Task.CompletedTask;
        }

        public override Task PurgeReceiptsAsync(string scope, long throughVersion, CancellationToken cancellationToken = default)
        {
            For(scope).PurgeReceipts(throughVersion);
            return Task.CompletedTask;
        }

        public override Task BeginNewEpochAsync(long versionFloor, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                foreach (var scope in _servers.Keys.ToList())
                {
                    _servers[scope] = Create(_servers[scope].CreateBackup(), versionFloor);
                }
            }

            return Task.CompletedTask;
        }

        private InMemorySyncServer<ConformanceDocument> For(string scope)
        {
            lock (_gate)
            {
                if (!_servers.TryGetValue(scope, out var server))
                {
                    _servers[scope] = server = Create(restoreFrom: null, versionFloor: 0);
                }

                return server;
            }
        }

        private InMemorySyncServer<ConformanceDocument> Create(InMemorySyncServerBackup<ConformanceDocument>? restoreFrom, long versionFloor) =>
            new(new InMemorySyncServerOptions<ConformanceDocument>
            {
                Cloner = Clone,
                Fingerprint = Fingerprint,
                PhysicalClock = options.Clock,
                MaxClockSkew = options.MaxClockSkew,
                Validator = options.Validator,
                CanRead = options.CanRead,
                ScopeFingerprint = options.ScopeFingerprint,
                Readers = options.Readers,
                PrincipalKey = options.PrincipalKey,
                RestoreFrom = restoreFrom,
                VersionFloor = versionFloor,
            });

        private sealed class Router(InMemoryAuthorityUnderTest owner) : ISyncAuthority<ConformanceDocument>, ISyncPublisher<ConformanceDocument>
        {
            public AuthorityLimits Limits { get; } = new(1000, 1000);

            public Task<PullResult<ConformanceDocument>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(context);
                return owner.For(context.Scope).PullAsync(context, request, cancellationToken);
            }

            public Task<PushResult<ConformanceDocument>> PushAsync(SyncCallContext context, PushRequest<ConformanceDocument> request, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(context);
                return owner.For(context.Scope).PushAsync(context, request, cancellationToken);
            }

            public Task<SyncPublishResult> UpsertAsync(string scope, ConformanceDocument document, DbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
                owner.For(scope).UpsertAsync(scope, document, transaction, cancellationToken);

            public Task<SyncPublishResult> DeleteAsync(string scope, string id, DbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
                owner.For(scope).DeleteAsync(scope, id, transaction, cancellationToken);

            public Task<SyncPublishResult> ReplaceScopeAsync(string scope, IEnumerable<ConformanceDocument> documents, DbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
                owner.For(scope).ReplaceScopeAsync(scope, documents, transaction, cancellationToken);

            public async Task<SyncPublishResult> PublishAsync(ConformanceDocument document, IEnumerable<string> scopes, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(scopes);
                var result = SyncPublishResult.None;
                foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
                {
                    result = result.Add(await owner.For(scope).UpsertAsync(scope, document, transaction, cancellationToken).ConfigureAwait(false));
                }

                return result;
            }
        }
    }
}
