// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Extensions;

/// <summary>
/// Builds the application's Content-Security-Policy and registers a middleware that emits the
/// standard set of security headers. Centralised here so <c>Program.cs</c> stays focused on
/// host wiring.
/// </summary>
internal static class SecurityHeadersExtensions
{
    public static WebApplication UseAetheusSecurityHeaders(
        this WebApplication app,
        IEnumerable<string> corsOrigins)
    {
        // Allow required WASM evaluation and blob workers while restricting styles to self and
        // connections to self plus configured frontend origins, including their WebSocket
        // equivalents. Compute the policy at startup without global ws/wss wildcards.
        // 'report-sample' allows nothing: it only asks the browser to put the first 40 characters of a
        // blocked inline script in the report, which the four reports of recette R1-83 lacked.
        var cspConnectSources = new List<string> { "'self'" };
        foreach (var origin in corsOrigins.Distinct())
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            {
                cspConnectSources.Add(origin);
                var wsScheme = originUri.Scheme == "https" ? "wss" : "ws";
                cspConnectSources.Add($"{wsScheme}://{originUri.Authority}");
            }
        }
        var contentSecurityPolicy =
            "default-src 'self'; " +
            "script-src 'self' 'wasm-unsafe-eval' blob: 'report-sample'; " +
            "worker-src 'self' blob:; " +
            "style-src 'self'; " +
            "img-src 'self' data: blob:; " +
            "font-src 'self' data:; " +
            $"connect-src {string.Join(' ', cspConnectSources.Distinct())}; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'; " +
            // Send reporting directives with the legacy fallback for browsers that require it.
            "report-uri /api/security/csp-report; " +
            "report-to csp-endpoint";

        // The Reporting API endpoint the report-to directive above refers to.
        const string reportingEndpoints = "csp-endpoint=\"/api/security/csp-report\"";

        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
            context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
            context.Response.Headers.Append("Reporting-Endpoints", reportingEndpoints);
            context.Response.Headers.Append("Content-Security-Policy", contentSecurityPolicy);
            await next();
        });

        return app;
    }
}
