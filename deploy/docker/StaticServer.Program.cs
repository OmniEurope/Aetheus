// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aetheus.WebAnalytics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;

var settingsPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "appsettings.json");
var apiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL");
if (File.Exists(settingsPath))
{
    var appVersion = Environment.GetEnvironmentVariable("APP_VERSION");
    if (!string.IsNullOrWhiteSpace(apiBaseUrl) || !string.IsNullOrWhiteSpace(appVersion))
    {
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))?.AsObject()
            ?? throw new InvalidOperationException("Frontend appsettings.json must contain a JSON object.");
        if (!string.IsNullOrWhiteSpace(apiBaseUrl)) settings["ApiBaseUrl"] = apiBaseUrl;
        if (!string.IsNullOrWhiteSpace(appVersion))
        {
            var appSettings = settings["App"] as JsonObject ?? new JsonObject();
            settings["App"] = appSettings;
            appSettings["Version"] = appVersion;
        }

        var temporaryPath = settingsPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, settingsPath, overwrite: true);
        File.Delete(settingsPath + ".br");
        File.Delete(settingsPath + ".gz");
    }
}

var builder = WebApplication.CreateBuilder(args);

// The Blazor WASM bundle (.wasm/.dll/.json/.js) is highly compressible and was
// served UNCOMPRESSED - the dominant cause of slow first paint in production.
// Brotli (gzip fallback) cuts the payload ~4-6x. Static file middleware emits
// ETag/Last-Modified so repeat loads are 304s (no recompression); responses that
// are already content-encoded are passed through untouched (no double work).
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "application/wasm",
        "application/octet-stream",
        "application/manifest+json",
        "image/svg+xml"
    ]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
// This server holds no session (the application talks to the API with a bearer token), so the
// signed-in visitor is named by the opaque identifier the application declares.
builder.Services.AddAetheusWebAnalytics(builder.Configuration, options => options.AcceptDeclaredUserId = true);

var app = builder.Build();

// Trusts Apache-forwarded visitor addresses because Compose binds the container to loopback and the
// local vhost sets the header. This trust boundary requires the container to remain inaccessible on
// public interfaces.
var forwardedHeadersOptions = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseResponseCompression();

var cspConnectSources = new List<string> { "'self'" };
if (Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiUri))
{
    cspConnectSources.Add(apiUri.GetLeftPart(UriPartial.Authority));
    var webSocketScheme = apiUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
    cspConnectSources.Add($"{webSocketScheme}://{apiUri.Authority}");
}

// Blazor's published index contains a generated inline import map. The shell also has two
// deliberately inline boot snippets that must run before/after the framework loader. Keep
// script-src strict by authorizing only the exact inline content shipped in this image.
// HTML parsing normalizes CRLF/CR to LF before CSP hashes are evaluated, so hash that form.
var indexPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
var inlineScriptHashes = new List<string>();
if (File.Exists(indexPath))
{
    var indexHtml = await File.ReadAllTextAsync(indexPath);
    foreach (Match match in Regex.Matches(
                 indexHtml,
                 "<script\\b(?![^>]*\\bsrc\\s*=)[^>]*>(?<content>.*?)</script>",
                 RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
    {
        var normalizedContent = match.Groups["content"].Value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedContent)));
        inlineScriptHashes.Add($"'sha256-{hash}'");
    }
}

// Reports frontend CSP violations to the API through report-to and Reporting-Endpoints, avoiding
// deprecated report-uri. Browsers without Reporting API support still enforce CSP but send no
// reports.
var cspReportUri = Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var reportApi)
    ? $"{reportApi.GetLeftPart(UriPartial.Authority)}/api/security/csp-report"
    : null;
var reportingEndpoints = cspReportUri is null ? null : $"{CspReporting.EndpointName}=\"{cspReportUri}\"";

// 'report-sample' allows nothing: it only puts the first 40 characters of a blocked inline script in the
// report, so the next blocked injection says what it was (recette R1-83: four reports without a sample).
var contentSecurityPolicy =
    "default-src 'self'; " +
    $"script-src 'self' 'wasm-unsafe-eval' blob: 'report-sample' {string.Join(' ', inlineScriptHashes.Distinct())}; " +
    "worker-src 'self' blob:; " +
    // The application shell forbids inline style attributes and elements. Monaco has a separate
    // document policy for the inline styles required by its renderer.
    "style-src 'self'; " +
    "style-src-elem 'self'; " +
    "style-src-attr 'none'; " +
    "img-src 'self' data: blob:; " +
    "font-src 'self' data:; " +
    $"connect-src {string.Join(' ', cspConnectSources.Distinct())}; " +
    "frame-ancestors 'none'; " +
    "base-uri 'self'; " +
    "form-action 'self'" +
    CspReporting.Directive(cspReportUri);

// QA scans this container directly, before an Apache reverse proxy is involved. Emit the same
// browser protections here so static assets, fallback HTML and error responses are all covered.
app.Use(async (context, next) =>
{
    var isMonacoFrame = context.Request.Path.Equals(MonacoFramePolicy.Path, StringComparison.OrdinalIgnoreCase)
        || context.Request.Path.Equals(MonacoFramePolicy.OmniPath, StringComparison.OrdinalIgnoreCase);
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (isMonacoFrame)
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    else
        context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (reportingEndpoints is not null)
        context.Response.Headers["Reporting-Endpoints"] = reportingEndpoints;
    context.Response.Headers["Content-Security-Policy"] = isMonacoFrame
        ? MonacoFramePolicy.Build(cspReportUri)
        : contentSecurityPolicy;
    await next();
});

// The published index contains the generated import map for the exact Blazor
// runtime files shipped by this image. Reusing it after a blue/green switch can
// therefore point an existing browser at framework hashes that only existed in
// the previous container. The HTML shell must never outlive the deployment that
// produced it. The fingerprinted framework files are immutable; the other shipped
// assets are revalidated through their ETag/Last-Modified headers (cheap 304s).
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var isHtml = context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
        var isFramework = context.Request.Path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase);
        var isVersionCoupledComponentAsset = context.Request.Path.StartsWithSegments(
            "/_content/OmniEurope.Blazor", StringComparison.OrdinalIgnoreCase);
        var isUnfingerprintedAsset = isVersionCoupledComponentAsset
            || context.Request.Path.StartsWithSegments("/css", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/js", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/help", StringComparison.OrdinalIgnoreCase);

        var isServiceWorker = context.Request.Path.Equals("/service-worker.js", StringComparison.OrdinalIgnoreCase);
        // Appsettings.json is rewritten at every container start (API_BASE_URL, APP_VERSION), so,
        // like the HTML shell, it must never outlive the deployment that wrote it.
        var isRuntimeSettings = context.Request.Path.Equals("/appsettings.json", StringComparison.OrdinalIgnoreCase);
        if (isHtml || isServiceWorker || isRuntimeSettings)
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        }
        else if (isFramework)
        {
            // Content-hashed framework assets are immutable for one year to avoid repeated
            // downloads. Missing assets must not be cached because their names can be requested
            // during a deployment swap or rollback.
            if (context.Response.StatusCode < 400)
            {
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                context.Response.Headers.Remove("Pragma");
                context.Response.Headers.Remove("Expires");
            }
            else
            {
                context.Response.Headers.CacheControl = "no-store";
            }
        }
        else if (isUnfingerprintedAsset)
        {
            // Revalidates non-hashed assets on every use so ETags keep component resources aligned
            // with their assemblies.
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Remove("Pragma");
            context.Response.Headers.Remove("Expires");
        }

        return Task.CompletedTask;
    });

    await next();
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapGet("/aetheus-analytics/v1/configuration", (AetheusWebAnalyticsOptions options) =>
    Results.Ok(new { enabled = options.Enabled }));
app.MapAetheusWebAnalytics();
app.MapFallbackToFile("index.html");

app.Run();
