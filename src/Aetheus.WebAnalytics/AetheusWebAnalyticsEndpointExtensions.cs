// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aetheus.WebAnalytics;

public static class AetheusWebAnalyticsEndpointExtensions
{
    private const int MaxEventBodyBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAetheusWebAnalytics(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<AetheusWebAnalyticsOptions>();
        if (!options.Enabled)
            return endpoints;

        // nosemgrep: aetheus.csharp.antiforgery-disabled - anonymous analytics beacon, carries no session or cookie-authenticated action.
        endpoints.MapPost("/aetheus-analytics/v1/events", CollectAsync)
            .DisableAntiforgery();
        // nosemgrep: aetheus.csharp.antiforgery-disabled - anonymous analytics beacon, carries no session or cookie-authenticated action.
        endpoints.MapPost("/aetheus-analytics/v1/opt-out", OptOut)
            .DisableAntiforgery();
        // nosemgrep: aetheus.csharp.antiforgery-disabled - anonymous analytics beacon, carries no session or cookie-authenticated action.
        endpoints.MapPost("/aetheus-analytics/v1/opt-in", OptIn)
            .DisableAntiforgery();

        if (options.EnablePrivacyPage)
            endpoints.MapRazorPages();

        return endpoints;
    }

    private static async Task<IResult> CollectAsync(
        HttpContext context,
        IHostEnvironment environment,
        AetheusWebAnalyticsOptions options,
        AnalyticsPseudonymizer pseudonymizer,
        AnalyticsExportQueue queue,
        AnalyticsRequestRateLimiter rateLimiter,
        TimeProvider timeProvider)
    {
        if (options.ProductionOnly && !environment.IsProduction())
            return Results.NoContent();
        if (await AnalyticsPrivacyPolicy.IsOptedOutAsync(context, options).ConfigureAwait(false)
            || AnalyticsPrivacyPolicy.IsExcludedRequest(context, options))
            return Results.NoContent();
        if (!rateLimiter.TryAcquire(context))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var analyticsEvent = await ReadEventAsync(context).ConfigureAwait(false);
        if (analyticsEvent.Status is not null)
            return analyticsEvent.Status;
        var payload = analyticsEvent.Value!;
        if (!IsValid(payload))
            return Results.BadRequest();
        if (!IsSupportedPayload(payload))
            return Results.UnprocessableEntity();

        var now = timeProvider.GetUtcNow();
        if (payload.OccurredAtUtc < now.AddMinutes(-5)
            || payload.OccurredAtUtc > now.AddMinutes(5))
            return Results.UnprocessableEntity();

        string route;
        try
        {
            route = AnalyticsPrivacyPolicy.NormalizeRoute(payload.Route);
        }
        catch (InvalidOperationException)
        {
            return Results.UnprocessableEntity();
        }

        queue.TryWrite(pseudonymizer.Create(context, payload, route));
        return Results.Accepted();
    }

    private static async Task<(AnalyticsBrowserEvent? Value, IResult? Status)> ReadEventAsync(
        HttpContext context)
    {
        if (context.Request.ContentLength > MaxEventBodyBytes)
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

        var buffer = new byte[MaxEventBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total),
                    context.RequestAborted)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        if (total > MaxEventBodyBytes)
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

        try
        {
            var value = JsonSerializer.Deserialize<AnalyticsBrowserEvent>(
                buffer.AsSpan(0, total),
                JsonOptions);
            return value is null ? (null, Results.BadRequest()) : (value, null);
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest());
        }
    }

    private static async Task<IResult> OptOut(
        HttpContext context,
        AetheusWebAnalyticsOptions options)
    {
        if (!IsSameOrigin(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var authenticatedResult = await WriteAuthenticatedPreferenceAsync(context, options, optedOut: true).ConfigureAwait(false);
        if (authenticatedResult is not null) return authenticatedResult;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.Cookies.Append(AnalyticsPrivacyPolicy.OptOutCookieName, "1", PreferenceCookieOptions());
        }
        return Results.NoContent();
    }

    private static async Task<IResult> OptIn(
        HttpContext context,
        AetheusWebAnalyticsOptions options)
    {
        if (!IsSameOrigin(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var authenticatedResult = await WriteAuthenticatedPreferenceAsync(context, options, optedOut: false).ConfigureAwait(false);
        if (authenticatedResult is not null) return authenticatedResult;
        context.Response.Cookies.Delete(AnalyticsPrivacyPolicy.OptOutCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/"
        });
        return Results.NoContent();
    }

    private static async Task<IResult?> WriteAuthenticatedPreferenceAsync(
        HttpContext context,
        AetheusWebAnalyticsOptions options,
        bool optedOut)
    {
        if (context.User.Identity?.IsAuthenticated != true) return null;
        if (options.AuthenticatedOptOutWriter is null)
        {
            var action = optedOut ? "opt-out" : "opt-in";
            return Results.Problem(
                $"Authenticated {action} requires a host account preference writer.",
                statusCode: StatusCodes.Status501NotImplemented);
        }

        await options.AuthenticatedOptOutWriter(context, optedOut).ConfigureAwait(false);
        return null;
    }

    private static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;
        return string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    internal static CookieOptions PreferenceCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        MaxAge = TimeSpan.FromDays(365),
        Path = "/"
    };

    private static bool IsValid(AnalyticsBrowserEvent analyticsEvent)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(
            analyticsEvent,
            new ValidationContext(analyticsEvent),
            results,
            validateAllProperties: true);
    }

    private static bool IsSupportedPayload(AnalyticsBrowserEvent analyticsEvent) =>
        analyticsEvent.Kind switch
        {
            // A heartbeat keeps the visit's LastSeenAtUtc fresh on the current route; the backend
            // ingest accepts it and never counts it as a page view.
            "page_view" or "heartbeat" => analyticsEvent.DurationMs is null && analyticsEvent.ErrorType is null,
            "browser_performance" => analyticsEvent.DurationMs is not null
                                     && analyticsEvent.ErrorType is null,
            "browser_error" => analyticsEvent.DurationMs is null
                               && analyticsEvent.ErrorType is "script_error"
                                   or "unhandled_rejection",
            _ => false
        };
}
