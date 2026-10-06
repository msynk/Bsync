using System.Security.Claims;
using System.Text;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Server.AspNetCore.Blobs;
using Bsync.Server.Blobs.S3;
using Bsync.Server.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bsync.Samples.Tasks.Server;

/// <summary>Builds the sample server (also used by its automated test).</summary>
public static class TasksServer
{
    // Development only. A real application validates tokens from its identity provider instead.
    private const string DevelopmentSigningKey = "bsync-sample-development-signing-key-not-a-secret";

    public static async Task<WebApplication> BuildAsync(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var connectionString = builder.Configuration.GetConnectionString("Tasks")
            ?? throw new InvalidOperationException("Set ConnectionStrings:Tasks to the application's SQL Server database.");
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Tasks:SigningKey"] ?? DevelopmentSigningKey));

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKey = signingKey,
                NameClaimType = "sub",
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, TasksJson.Default));

        // Attachments (task F1): verified content in a directory, or in an S3-compatible bucket when configured.
        var blobDirectory = builder.Configuration["Tasks:BlobDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "blobs");
        IBlobStore blobStore = builder.Configuration["Tasks:S3:ServiceUrl"] is { Length: > 0 } serviceUrl
            ? new S3BlobStore(
                new Amazon.S3.AmazonS3Client(
                    new Amazon.Runtime.BasicAWSCredentials(builder.Configuration["Tasks:S3:AccessKey"], builder.Configuration["Tasks:S3:SecretKey"]),
                    new Amazon.S3.AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true, AuthenticationRegion = builder.Configuration["Tasks:S3:Region"] ?? "us-east-1" }),
                builder.Configuration["Tasks:S3:Bucket"] ?? throw new InvalidOperationException("Set Tasks:S3:Bucket."),
                new FileSystemBlobStore(blobDirectory))
            {
                PresignHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
                PresignUploads = true,
            }
            : new FileSystemBlobStore(blobDirectory);
        builder.Services.AddSingleton(blobStore);
        builder.Services.AddSingleton(new BlobJanitor(connectionString, blobStore, TimeProvider.System));

        var app = builder.Build();
        await TasksDb.EnsureTablesAsync(connectionString);
        var authority = await SqlServerSyncAuthority<TaskDocument>.CreateAsync(new SqlServerSyncAuthorityOptions<TaskDocument>
        {
            ConnectionString = connectionString,
            DocumentType = TasksJson.Default.TaskDocument,
            Collection = TasksJson.Collection,
            WriteHandler = new TaskWriteHandler(),
        });

        // Intents (task F3) live in their own collection; their handler changes tasks in the same transaction.
        var intents = await SqlServerSyncAuthority<TaskIntent>.CreateAsync(new SqlServerSyncAuthorityOptions<TaskIntent>
        {
            ConnectionString = connectionString,
            DocumentType = TasksJson.Default.TaskIntent,
            Collection = TasksJson.IntentCollection,
            WriteHandler = new IntentWriteHandler(authority),
        });

        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();

        // Development sign-in: a bearer token for any user and tenant. Replace with your identity provider.
        if (app.Configuration.GetValue("Tasks:DevelopmentTokens", true))
        {
            var tokens = new JsonWebTokenHandler();
            app.MapPost("/api/token", (TokenRequest request) =>
                !SyncIds.IsValid(request.User) || !SyncIds.IsValid(request.Tenant)
                    ? Results.BadRequest()
                    : Results.Ok(new TokenResponse(tokens.CreateToken(new SecurityTokenDescriptor
                    {
                        Claims = new Dictionary<string, object> { ["sub"] = request.User, ["tenant"] = request.Tenant },
                        Expires = DateTime.UtcNow.AddHours(1),
                        SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
                    }))));
        }

        // An ordinary API endpoint (a back office, an import, a job would look the same): it writes the application's
        // table with EF Core and publishes the replicated document in the same transaction (task B3).
        app.MapPost("/api/tasks", async (NewTask request, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var tenant = user.FindFirst("tenant")?.Value;
            if (tenant is null || !SyncIds.IsValid(request.Id))
            {
                return Results.BadRequest();
            }

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.Problem(statusCode: 400, title: TaskRules.TitleRequired);
            }

            var document = new TaskDocument { Id = request.Id, Title = request.Title, Slug = TaskRules.Slug(request.Title) };
            await using var db = TasksDb.Open(connectionString);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            document.Revision = 1;
            db.Tasks.Add(new TaskEntity { Tenant = tenant, Id = document.Id, Title = document.Title, Slug = document.Slug, Revision = 1, ChangedBy = user.FindFirst("sub")?.Value });
            await db.SaveChangesAsync(cancellationToken);
            await authority.UpsertAsync(tenant, document, transaction.GetDbTransaction(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            authority.NotifyCommitted(new AuthorityCommit(tenant, [document.Id]));
            return Results.Created($"/api/tasks/{document.Id}", document);
        }).RequireAuthorization();

        // Bundles (task F2): published by the server through an API endpoint, read by replicas.
        var bundles = await SqlServerSyncAuthority<BundleManifest>.CreateAsync(new SqlServerSyncAuthorityOptions<BundleManifest>
        {
            ConnectionString = connectionString,
            DocumentType = TasksJson.Default.BundleManifest,
            Collection = TasksJson.BundleCollection,
            WriteHandler = new ReadOnlyWriteHandler<BundleManifest>(),
        });
        BundleEndpoints.Map(app, connectionString, bundles);

        // Attachments (task F1): resumable uploads and range downloads next to the sync routes.
        var syncOptions = new SyncEndpointOptions
        {
            SupportedSchemas = new HashSet<string>([TasksJson.SchemaId], StringComparer.Ordinal),
            ResolveScope = http => http.User.FindFirst("tenant")?.Value,
        };
        app.MapSyncBlobs(blobStore, new TasksBlobAccess(connectionString), syncOptions).RequireAuthorization();

        app.MapSyncCollections(
            syncOptions,
            collections => collections
                .Add(TasksJson.Collection, authority, SyncJsonTypes<TaskDocument>.From(TasksJson.Default))
                .Add(TasksJson.IntentCollection, intents, SyncJsonTypes<TaskIntent>.From(TasksJson.Default))
                .Add(TasksJson.BundleCollection, bundles, SyncJsonTypes<BundleManifest>.From(TasksJson.Default)))
            .RequireAuthorization();

        app.MapFallbackToFile("index.html");
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            bundles.DisposeAsync().AsTask().GetAwaiter().GetResult();
            intents.DisposeAsync().AsTask().GetAwaiter().GetResult();
            authority.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
        return app;
    }
}
