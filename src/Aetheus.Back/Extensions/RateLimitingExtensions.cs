// SPDX-License-Identifier: EUPL-1.2
using System.Threading.RateLimiting;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace Aetheus.Back.Extensions;

internal static class RateLimitingExtensions
{
    /// <summary>
    /// Registers the named ingress policies and the authenticated global ceiling.
    /// Integration fixtures can disable the named policies without changing production defaults.
    /// </summary>
    public static IServiceCollection AddAetheusRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddRateLimiter(options => ConfigureRateLimiter(options, configuration));
        return services;
    }

    private static void ConfigureRateLimiter(RateLimiterOptions options, IConfiguration configuration)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = WriteRejectionAsync;
        if (configuration.GetValue("RateLimiting:Disabled", false))
        {
            AddDisabledPolicies(options);
            return;
        }

        AddAuthenticationPolicies(options);
        AddPublicEndpointPolicies(options);
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ResolveGlobalPartition);
    }

    /// <summary>Audits a throttled login attempt, then answers the refusal as a JSON <see cref="ApiError"/>.</summary>
    internal static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken ct)
    {
        if (context.HttpContext.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase))
            await AuditRateLimitedLoginAsync(context.HttpContext, ct);

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ApiError { Message = "Too many authentication attempts. Try again shortly." }, ct);
    }

    private static async Task AuditRateLimitedLoginAsync(HttpContext httpContext, CancellationToken ct)
    {
        var audit = httpContext.RequestServices.GetRequiredService<IAuditService>();
        var ip = RemoteAddress(httpContext);
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        var cleanedUserAgent = new string(userAgent.Where(c => !char.IsControl(c)).Take(200).ToArray());
        await audit.LogAsync("LoginFailed.RateLimited", "Authentication", details:
            $"IP: {ip}, UA: {(string.IsNullOrEmpty(cleanedUserAgent) ? "unknown" : cleanedUserAgent)}", ct: ct);
    }

    private static void AddAuthenticationPolicies(RateLimiterOptions options)
    {
        options.AddPolicy("login", context =>
            RateLimitPartition.GetFixedWindowLimiter(RemoteAddress(context), _ => FixedWindow(20)));
        options.AddPolicy("enrollment", context =>
            RateLimitPartition.GetFixedWindowLimiter(RemoteAddress(context), _ => FixedWindow(5)));
        options.AddPolicy("auth-token", context =>
            RateLimitPartition.GetFixedWindowLimiter(ResolveAuthTokenPartitionKey(context), _ => FixedWindow(60)));
        options.AddPolicy("webhook", context =>
            RateLimitPartition.GetFixedWindowLimiter(RemoteAddress(context), _ => FixedWindow(30)));
        // R-14: forgotten-password requests per address of the caller; the per-account limit is in the service.
        options.AddPolicy("password-reset", context =>
            RateLimitPartition.GetFixedWindowLimiter("password-reset:" + RemoteAddress(context), _ => FixedWindow(5)));
        // R-14: set-password links an administrator sends, per administrator.
        options.AddPolicy("password-link", context =>
            RateLimitPartition.GetFixedWindowLimiter("password-link:" + ResolveAuthTokenPartitionKey(context), _ => FixedWindow(10)));
    }

    private static void AddPublicEndpointPolicies(RateLimiterOptions options)
    {
        // Fail closed before ingest-key authentication: an untrusted caller can rotate arbitrary
        // header values, so a key-derived bucket would grant a fresh allowance on every request.
        // A validated app identity is not available at middleware time; partition by address here
        // and let the ingest authentication/cache enforce the per-app boundary afterwards.
        options.AddPolicy("otlp-ingest", context =>
            RateLimitPartition.GetFixedWindowLimiter(ResolveOtlpPartitionKey(context), _ => FixedWindow(120)));
        options.AddPolicy("web-analytics-public", context =>
            RateLimitPartition.GetFixedWindowLimiter("analytics-address:" + RemoteAddress(context), _ => FixedWindow(60)));
        // A browser posts one report per violation; a broken page could post many, so this has
        // its own partition rather than eating the global budget.
        options.AddPolicy("csp-report", context =>
            RateLimitPartition.GetFixedWindowLimiter("csp-report:" + RemoteAddress(context), _ => FixedWindow(30)));
        options.AddPolicy("external-login", context =>
            RateLimitPartition.GetFixedWindowLimiter("external-login:" + RemoteAddress(context), _ => FixedWindow(5)));
    }

    /// <summary>The global ceiling: one shared fixed window for external sign-in, otherwise a sliding
    /// window per agent, user or address.</summary>
    internal static RateLimitPartition<string> ResolveGlobalPartition(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.EndsWith("/api/auth/external-login", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetFixedWindowLimiter("external-login-global", _ => FixedWindow(60));

        var (partitionKey, permitLimit) = ResolveGlobalLimit(context);
        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = BackendRuntimeDefaults.RateLimitWindow,
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    }

    internal static (string PartitionKey, int PermitLimit) ResolveGlobalLimit(HttpContext context)
    {
        if (string.Equals(
                context.User.Identity?.AuthenticationType,
                AgentTokenAuthenticationHandler.SchemeName,
                StringComparison.Ordinal))
        {
            var serverId = context.User.FindFirst("ServerId")?.Value ?? "unknown";
            return ($"agent:{serverId}", BackendRuntimeDefaults.AuthenticatedAgentRateLimit);
        }

        return context.User.Identity?.IsAuthenticated == true
            ? ($"u:{context.User.Identity.Name}", BackendRuntimeDefaults.AuthenticatedUserRateLimit)
            : ($"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                BackendRuntimeDefaults.AuthenticatedUserRateLimit);
    }

    internal static string ResolveAuthTokenPartitionKey(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? $"user:{context.User.Identity.Name}"
            : $"ip:{RemoteAddress(context)}";

    internal static string ResolveOtlpPartitionKey(HttpContext context) =>
        $"otlp-address:{RemoteAddress(context)}";

    private static string RemoteAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static void AddDisabledPolicies(RateLimiterOptions options)
    {
        foreach (var name in new[]
                 {
                     "login", "enrollment", "auth-token", "webhook", "external-login", "otlp-ingest",
                     "web-analytics-public", "csp-report", "password-reset", "password-link"
                 })
            options.AddPolicy(name, _ => RateLimitPartition.GetNoLimiter("all"));
    }

    private static FixedWindowRateLimiterOptions FixedWindow(int permitLimit) =>
        new()
        {
            PermitLimit = permitLimit,
            Window = BackendRuntimeDefaults.RateLimitWindow,
            QueueLimit = 0
        };
}
