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
// Recette R-471: this server holds no session (the application talks to the API with a bearer token),
// so the signed-in visitor is named by the opaque identifier the application declares.
builder.Services.AddAetheusWebAnalytics(builder.Configuration, options => options.AcceptDeclaredUserId = true);

var app = builder.Build();

// Recette R-471: behind Apache every request came from 127.0.0.1, so every visitor shared one network
// prefix (one "unique visitor"), one rate-limit bucket and one excluded-network test. The address is
// now the one Apache forwards. Clearing the known lists is safe for the same reason as in the backend:
// Compose binds this container to 127.0.0.1, so only the local vhost can reach it, and that vhost sets
// the header itself. Published on a public interface, the header would become spoofable.
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

// PLAN-003 lot 14 / D16: the front document's policy is this one, not the API's, so a violation on
// a front page was refused silently and reported nowhere. It reports to the API's collector.
// Recette R2-108 (ZAP 10055-3, "CSP: Notices"): the collector is named through report-to and the
// Reporting-Endpoints header, not report-uri. The scanner's CSP parser warns on report-uri whatever
// else the policy says ("deprecated in favor of the new report-to directive"), which was the only
// notice the front's policy raised. The Reporting API posts cross-origin to the API, which its
// default CORS policy already admits for the front's origin (POST, Content-Type), the same origin
// every API call of the front comes from. A browser without the Reporting API (Firefox) sends no
// violation report at all; it still enforces the policy.
var cspReportUri = Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var reportApi)
    ? $"{reportApi.GetLeftPart(UriPartial.Authority)}/api/security/csp-report"
    : null;
var reportingEndpoints = cspReportUri is null ? null : $"{CspReporting.EndpointName}=\"{cspReportUri}\"";

var contentSecurityPolicy =
    "default-src 'self'; " +
    $"script-src 'self' 'wasm-unsafe-eval' blob: {string.Join(' ', inlineScriptHashes.Distinct())}; " +
    "worker-src 'self' blob:; " +
    // PLAN-008 lot 44: the application shell refuses style attributes. Monaco runs in its own
    // document with a path-specific policy because it renders positioned text with inline styles.
    // The three other dependencies that used to require 'unsafe-inline' were lifted: the boot
    // splash carries its animation delays in classes, StageNode publishes its coordinates as data
    // attributes that a script copies through the CSSOM, and AetheusVirtualList replaced Blazor's
    // <Virtualize>, whose spacers were sized with a style attribute.
    //
    // Recette R-526: style-src-elem no longer carries 'unsafe-inline'. It was kept for the Monaco
    // editor, which creates <style> elements at runtime; but the editor now lives in its own document
    // (MonacoFramePolicy), and nothing else in the shell creates a style element: no script of
    // wwwroot outside lib/monaco-editor, none of OmniEurope.Blazor, no markup. The exception had
    // outlived its reason, which is what the DAST scan kept reporting.
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
        // Recette R-265: appsettings.json is rewritten at every container start (API_BASE_URL,
        // APP_VERSION), so, like the HTML shell, it must never outlive the deployment that wrote it.
        var isRuntimeSettings = context.Request.Path.Equals("/appsettings.json", StringComparison.OrdinalIgnoreCase);
        if (isHtml || isServiceWorker || isRuntimeSettings)
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        }
        else if (isFramework)
        {
            // PLAN-003 lot 13 / D17: the same policy the production vhost applies, so the container
            // mirror and the demo behave like production rather than only looking like it. Every
            // file under /_framework carries .NET's own content hash in its name, so a given name
            // can never be served different bytes; a year of immutable is safe and is what stops
            // the runtime being re-downloaded on each visit.
            //
            // Only for a file actually served. A 404 under /_framework (a hash asked for during a
            // blue/green swap or a rollback) marked immutable would stay stuck in the browser for a
            // year; and a 404 is heuristically cacheable by default, so it is told not to be stored.
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
            // PLAN-003 lot 13 / D17: kept but revalidated on every use, as the vhost does. For the
            // component assets this still keeps them in step with their assembly: a new version
            // changes the ETag, so the browser never runs a stale copy, it only stops paying for an
            // unchanged one.
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
