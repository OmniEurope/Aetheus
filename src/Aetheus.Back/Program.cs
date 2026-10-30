// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Components.PersonalAccessTokens;
using Aetheus.Back.Configuration;
using Aetheus.Back.Extensions;
using Aetheus.Back.Hubs;
using Aetheus.Back.Middleware;
using Aetheus.Back.Services.Email;
using Aetheus.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddAetheusFileLogging();

// Self-observability is deployment-controlled and remains disabled when the injected
// AETHEUS_TELEMETRY_ENABLED variable is absent. Production deploy stages receive a
// short-lived ingest key from the monitored application selected for this project.
builder.Services.AddAetheusTelemetry(builder.Configuration, options =>
{
    options.ApplicationVersion = builder.Configuration["App:Version"];
    options.EnvironmentName = builder.Environment.EnvironmentName.ToLowerInvariant();
    // Measure application database sizes through the existing connection only when telemetry is
    // enabled.
    options.DatabaseSize.UsePostgreSql(() => new NpgsqlConnection(builder.Configuration.GetConnectionString("Default")));
});

// --- Time ---
builder.Services.AddSingleton(TimeProvider.System);
// RequestErrorLoggingMiddleware counts the refused requests that match no route.
builder.Services.AddSingleton<UnmatchedRequestCounter>();
// R1-40: a request past the threshold logs what the process was doing meanwhile (GC, pool, background work).
builder.Services.AddSlowRequestDiagnostics(builder.Configuration);

// --- Data Protection ---
builder.AddAetheusDataProtection();

// --- Database (PostgreSQL only) ---
builder.AddAetheusDatabase();

// --- Authentication ---
// F-37: bind strongly typed options for the Auth section (consumers can inject IOptions<T>).
builder.Services.Configure<Aetheus.Back.Configuration.AuthOptions>(
    builder.Configuration.GetSection(Aetheus.Back.Configuration.AuthOptions.SectionName));
builder.Services.Configure<Aetheus.Back.Configuration.AgentTokenOptions>(
    builder.Configuration.GetSection(Aetheus.Back.Configuration.AgentTokenOptions.SectionName));
builder.Services.Configure<Aetheus.Back.Configuration.EncryptionOptions>(
    builder.Configuration.GetSection(Aetheus.Back.Configuration.EncryptionOptions.SectionName));

// F-01: refuses publicly known or missing secrets outside Development.
var jwtKey = StartupSecrets.ResolveJwtSigningKey(builder.Configuration, builder.Environment.IsDevelopment());
// Single, validated source of truth for JWT signing - services must inject JwtOptions and never
// re-resolve Auth:JwtKey from configuration.
var jwtOptions = new JwtOptions { SigningKey = jwtKey };
builder.Services.AddSingleton(jwtOptions);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = BackendRuntimeDefaults.JwtClockSkew
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Allow SignalR to receive token from query string (only for /hubs paths).
                // Browser WebSocket clients cannot set Authorization headers, so SignalR passes
                // the JWT as the `access_token` query param - standard and unavoidable.
                // LOG-HYGIENE INVARIANT: this token must never reach the logs. ASP.NET Core's
                // `Microsoft.AspNetCore.Hosting.Diagnostics` emits a "Request starting ... ?query"
                // line (which would include access_token) at Information level. Both appsettings
                // pin `Microsoft.AspNetCore` to `Warning`, suppressing that line in every sink
                // (console + the JSON file logger). Do NOT lower that category to Information
                // without first redacting the access_token query param, or tokens will be logged.
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                // Immediate revocation: reject tokens whose SecurityStamp claim no longer matches
                // the user's current persisted stamp (bumped on password / role change / logout).
                var principal = context.Principal;
                if (principal is null) return;

                var nameIdClaim = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
                var stampClaim = principal.FindFirst(AetheusClaimTypes.SecurityStamp);

                if (nameIdClaim is null || string.IsNullOrWhiteSpace(nameIdClaim.Value)
                    || stampClaim is null || string.IsNullOrWhiteSpace(stampClaim.Value))
                {
                    context.Fail("Token is missing mandatory revocation claims.");
                    return;
                }

                // Hardening (#19): bootstrap admin tokens are now revocable too - they carry
                // a deterministic NameIdentifier="bootstrap" + SecurityStamp="bootstrap". The
                // operator can rotate this stamp via Auth:BootstrapStamp config, instantly
                // invalidating outstanding bootstrap tokens.
                if (nameIdClaim.Value == "bootstrap")
                {
                    var cfg = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                    var currentStamp = cfg["Auth:BootstrapStamp"] ?? "bootstrap";
                    if (!CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(stampClaim.Value),
                            Encoding.UTF8.GetBytes(currentStamp)))
                        context.Fail("Bootstrap token security stamp is no longer valid.");
                    return;
                }

                if (!int.TryParse(nameIdClaim.Value, out var userId))
                {
                    context.Fail("Token subject is invalid.");
                    return;
                }

                var auth = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();
                if (!await auth.IsSecurityStampValidAsync(userId, stampClaim.Value, context.HttpContext.RequestAborted))
                    context.Fail("Token security stamp is no longer valid.");
            }
        };

        // JWT is the default Bearer scheme. Forward PAT-shaped tokens and endpoints explicitly
        // protected by the AgentToken policy before the global rate limiter evaluates the principal.
        options.ForwardDefaultSelector = BearerAuthenticationSchemeSelector.Resolve;
    })
    .AddScheme<AuthenticationSchemeOptions, AgentTokenAuthenticationHandler>(
        AgentTokenAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, GitBasicAuthenticationHandler>(
        GitBasicAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, PatAuthenticationHandler>(
        PatAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, PackageRegistryAuthenticationHandler>(
        PackageRegistryAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AgentToken", policy =>
        policy.AddAuthenticationSchemes(AgentTokenAuthenticationHandler.SchemeName)
              .RequireRole("Agent"));
});

// --- Services (Vertical Slice DI) ---
builder.Services.AddAetheusModules(builder.Configuration);

// --- Shared Services ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddScoped<ISecretMaskingRepository, SecretMaskingRepository>();
builder.Services.AddScoped<ISecretMaskingService, SecretMaskingService>();
builder.Services.AddScoped<IPermissionRepository, PermissionRepository>();
builder.Services.AddSingleton<AuthzCacheEvictor>();
builder.Services.AddScoped<IResourceAuthorizationService, ResourceAuthorizationService>();
builder.Services.AddSingleton<IEntityChangeNotifier, EntityChangeNotifier>();
builder.Services.AddSingleton<IAdminChangeNotifier, AdminChangeNotifier>();
builder.Services.AddSingleton<IUserChangeNotifier, UserChangeNotifier>();
builder.Services.AddScoped<IGitCliService, GitCliService>();
builder.Services.AddScoped<IDbTransactionScope, DbTransactionScope>();
builder.Services.AddEmailSending();

// Background services configuration (timeouts, intervals).
builder.Services.Configure<BackgroundServicesOptions>(
    builder.Configuration.GetSection("BackgroundServices"));

// --- Caching ---
builder.Services.AddMemoryCache();

// --- Request body size limit (F-024) ---
// Cap request bodies at 10 MB to bound memory / DoS exposure. Endpoints needing
// larger payloads can override per-action with [RequestSizeLimit]/[DisableRequestSizeLimit].
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
    o => o.Limits.MaxRequestBodySize = 10_485_760);

// --- SignalR ---
// SITE-SEC-002: every hub connection is tracked by user so a permission change can close them.
builder.Services.AddSingleton<Aetheus.Back.Hubs.HubConnectionRegistry>();
builder.Services.AddSignalR(options => Microsoft.AspNetCore.SignalR.HubOptionsExtensions.AddFilter<Aetheus.Back.Hubs.HubConnectionRegistryFilter>(options));

// --- Controllers ---
builder.Services.AddControllers(options =>
{
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(StatusCodes.Status401Unauthorized));
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(StatusCodes.Status403Forbidden));
}).AddJsonOptions(json =>
{
    json.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddScoped<LoginValidationAuditFilter>();

// Surface validation failures in the server logs so heartbeat / telemetry endpoints don't
// fail silently with a generic 400 response - the agent only sees the status code by default.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    var defaultFactory = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = ctx =>
    {
        var logger = ctx.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("ModelValidation");
        var errors = ctx.ModelState
            .Where(kv => kv.Value?.Errors.Count > 0)
            .Select(kv => $"{kv.Key}: {string.Join("; ", kv.Value!.Errors.Select(e => e.ErrorMessage))}");
        logger.LogWarning("400 ModelState invalid on {Method} {Path}: {Errors}",
            ctx.HttpContext.Request.Method,
            ctx.HttpContext.Request.Path,
            string.Join(" | ", errors));
        return defaultFactory(ctx);
    };
});

// --- Response compression ---
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = false;
    // F-015 (BREACH): application/json is explicitly excluded - in production Apache terminates
    // TLS and proxies plain HTTP to Kestrel, so EnableForHttps=false alone does not prevent
    // compressed JSON (which can reflect secrets) from being served over HTTPS.
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes
        .Concat(["text/plain"])
        .Where(m => !m.Contains("json", StringComparison.OrdinalIgnoreCase));
});

// --- API versioning (F-27) ---
// Default version v1.0 is assumed when callers omit the version. Negotiation strategy
// favours opt-in headers/query so existing clients continue to work without URL changes.
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new Asp.Versioning.ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = Asp.Versioning.ApiVersionReader.Combine(
        new Asp.Versioning.HeaderApiVersionReader("X-Api-Version"),
        new Asp.Versioning.QueryStringApiVersionReader("api-version"));
}).AddMvc();

// --- CORS ---
var corsOrigins = builder.ResolveCorsOrigins();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(corsOrigins.Distinct().ToArray())
            .WithHeaders("Authorization", "Content-Type", "X-Api-Version", "X-Requested-With", "x-signalr-user-agent", "x-requested-with", "X-Aetheus-Renewal")
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .WithExposedHeaders(RequestErrorLoggingMiddleware.CorrelationHeader,
                Aetheus.Back.Components.Auth.ForcedPasswordChangeMiddleware.RequiredHeader)
            .AllowCredentials()
            .SetPreflightMaxAge(BackendRuntimeDefaults.CorsPreflightMaxAge);
    });
});

builder.Services.AddAetheusRateLimiting(builder.Configuration);

// --- OpenAPI ---
// R-28: the generated OpenAPI document is kept for the process lifetime (see OpenApiDocumentCachePolicy).
builder.Services.AddOutputCache(o => o.AddPolicy(OpenApiDocumentCachePolicy.Name, b => b.AddPolicy<OpenApiDocumentCachePolicy>(), excludeDefaultPolicy: true));
builder.Services.AddOpenApi();
for (var partition = 0; partition < OpenApiDastPartition.Count; partition++)
{
    var currentPartition = partition;
    builder.Services.AddOpenApi(
        OpenApiDastPartition.DocumentName(currentPartition),
        options => options.ShouldInclude = description =>
            OpenApiDastPartition.Includes(description, currentPartition));
}

// --- Health Checks ---
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database");

var app = builder.Build();

// S-TECH-RTKP: the pipeline clone token (GitRunCloneToken) prefers a dedicated GitLight:RunTokenKey and
// falls back to the AES Auth:EncryptionKey (HKDF domain-separated, so safe). In production a dedicated
// key is cleaner: warn (don't fail) when it is absent so the operator can provision one.
if (app.Environment.IsProduction() && string.IsNullOrEmpty(app.Configuration["GitLight:RunTokenKey"]))
    app.Logger.LogWarning(
        "GitLight:RunTokenKey is not configured; pipeline clone tokens fall back to the HKDF-derived " +
        "Auth:EncryptionKey. Set GitLight__RunTokenKey to a dedicated secret for clearer key separation.");

// --- Database initialization ---
await app.InitializeDatabaseAsync();

// --- Middleware ---
// Behind the Apache reverse proxy (a loopback hop on the same host), restore the client's original
// scheme/IP from the X-Forwarded-* headers BEFORE anything downstream reads Request.Scheme/Request.Host
// - otherwise email links, OAuth redirects, 201 Location headers, and AgentInstaller URLs all emit
// http:// instead of https://. Must run first, ahead of HSTS/security-headers/auth.
//
// The defaults accept forwarded headers only from loopback, and the previous claim that this "is
// exactly the Apache hop here" holds solely when the backend runs directly on the host. In
// production it runs in a container, so Apache reaches it through the Docker bridge gateway, never
// 127.0.0.1: the defaults silently DROPPED X-Forwarded-Proto and every absolute URL came out as
// http://. That is what made `dotnet nuget push` read an http:// publish endpoint from the v3 index
// and refuse to publish, because NuGet requires HTTPS sources.
//
// Clearing both lists is safe HERE and only here: Compose binds the backend to 127.0.0.1:<port> on
// the host, so nothing but the local Apache vhost can open a connection to it, and that vhost sets
// X-Forwarded-Proto itself. If the port is ever published on a public interface these headers become
// spoofable and this must be revisited.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Use HTTPS redirection only when an HTTPS port is available, with forwarded headers supporting
// Apache termination. Apache owns the public redirect while local development retains HTTP for
// agent and test traffic.
if (!app.Environment.IsDevelopment() && HttpsPortConfiguration.IsKnown(app.Configuration))
    app.UseHttpsRedirection();

// S-FEAT-27: serve the OpenAPI spec in every environment so the in-app API Reference page can render
// it. In Development it stays anonymous (tooling/Scalar); elsewhere it is gated to administrators only
// - the in-app API Reference is an admin surface, so the spec must not be readable by any authenticated
// user (matching the nav, which already hides the page from non-admins).
if (app.Environment.IsDevelopment())
    app.MapOpenApi().CacheOutput(OpenApiDocumentCachePolicy.Name);
else
{
    app.MapOpenApi().RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" }).CacheOutput(OpenApiDocumentCachePolicy.Name);
    // F-26: enable HSTS in non-Development environments.
    app.UseHsts();
}

app.UseAetheusSecurityHeaders(corsOrigins);
app.UseMiddleware<SlowRequestDiagnosticsMiddleware>();
app.UseMiddleware<RequestErrorLoggingMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseResponseCompression();
// The public analytics endpoint validates its per-application origin against the database and
// emits its own narrow CORS headers. The global static policy must not short-circuit its preflight.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments(
        "/api/ingest/web-analytics/v1/public",
        StringComparison.OrdinalIgnoreCase),
    branch => branch.UseCors());
// UseRateLimiter MUST run after UseAuthentication so the GlobalLimiter sees a populated HttpContext.User
// (F-24): its per-user partition key reads User.Identity.IsAuthenticated, which is still false before
// authentication runs, collapsing every authenticated caller onto a shared per-IP bucket.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseOutputCache();
// Read-only Personal Access Token enforcement (ADR-024 4.5): runs after authorization so
// HttpContext.User carries the PAT principal; refuses mutating requests made with a read-only PAT.
app.UseMiddleware<PatScopeEnforcementMiddleware>();
// SITE-SEC-001: a token minted for an account that must change its password reaches only the change.
app.UseMiddleware<Aetheus.Back.Components.Auth.ForcedPasswordChangeMiddleware>();

// --- Agent server URL (used by AddAgent wizard) ---
app.MapGet("/api/servers/agent-server-url", (IConfiguration config, HttpContext httpContext) =>
    Results.Ok(new { Url = HostUrlExtensions.ResolvePublicApiUrl(config, httpContext) })
).RequireAuthorization();

// --- Agent binaries download (anonymous static files) ---
app.MapAgentDownloads();

// --- Endpoints ---
app.MapControllers();
app.MapHub<LogHub>("/hubs/logs");
app.MapHub<ServerHub>("/hubs/servers");
app.MapHub<PipelineHub>("/hubs/pipelines");
app.MapHub<ReleaseHub>("/hubs/releases");
app.MapHub<AlertHub>("/hubs/alerts");
app.MapHub<GitRealtimeHub>("/hubs/git");
app.MapHub<EntityHub>("/hubs/entities");
app.MapHub<AdminHub>("/hubs/admin");
app.MapHub<UserHub>("/hubs/user");
// F-18: /health (full checks incl. DB) requires authentication so unauthenticated callers
// can't probe service liveness. /health/live is an anonymous lightweight liveness probe
// for orchestrators / deploy scripts - it returns 200 as soon as the process is up.
app.MapHealthChecks("/health").RequireAuthorization();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

// Readiness probe (challenge, blue-green): /health/live is process-up only, so a blue-green flip could cut
// over to a colour whose DB isn't connected yet. /health/ready runs the "database" check and returns 200
// only when the app can actually serve DB-backed requests. It exposes no details (status text only), so it
// is safe to expose anonymously - the same signal as "does this colour serve" that the deploy already gates on.
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = reg => reg.Name == "database"
}).AllowAnonymous();

// Which backend instance answers this address: a random id drawn at startup, nothing else. Each
// blue-green colour compares it with its own through the public API address, so only the live colour
// takes the leader leases (LiveInstanceProbe). Anonymous like the probes above; it exposes no detail.
app.MapGet(LiveInstanceProbe.InstancePath, (ILiveInstanceProbe probe) => Results.Ok(new { instanceId = probe.InstanceId }))
    .AllowAnonymous()
    .ExcludeFromDescription();

app.Run();

public partial class Program { }
