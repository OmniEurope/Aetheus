// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Aetheus.Back.Middleware;

public sealed class RequestErrorLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestErrorLoggingMiddleware> logger,
    UnmatchedRequestCounter unmatched)
{
    public const string CorrelationHeader = "X-Aetheus-Correlation-Id";

    /// <summary>Longest reason written in a warning line; the client received it whole.</summary>
    internal const int MaxReasonLength = 300;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.TraceIdentifier;
        context.Response.Headers[CorrelationHeader] = correlationId;

        await next(context);

        var status = context.Response.StatusCode;
        if (status < StatusCodes.Status400BadRequest || status == 499)
            return;

        // A refused request that matched no route of the application (a robot probing /.env,
        // /wp-json...) is counted per minute, not written line by line. A 4xx on a real route and
        // every 5xx keep their own line.
        if (status < StatusCodes.Status500InternalServerError && context.GetEndpoint() is null)
        {
            unmatched.Record();
            return;
        }

        var level = status >= StatusCodes.Status500InternalServerError
            ? LogLevel.Error
            : IsIngestKeyRefusal(context) ? LogLevel.Debug
            : IsExpectedRefusal(context) ? LogLevel.Information : LogLevel.Warning;
        if (Reason(context) is { } reason)
            logger.Log(
                level,
                "HTTP {StatusCode} for {Method} {Path}: {Reason}; correlation {CorrelationId}",
                status,
                context.Request.Method,
                context.Request.Path,
                reason,
                correlationId);
        else
            logger.Log(
                level,
                "HTTP {StatusCode} for {Method} {Path}; correlation {CorrelationId}",
                status,
                context.Request.Method,
                context.Request.Path,
                correlationId);
    }

    /// <summary>The message <see cref="ErrorHandlingMiddleware"/> answered the refusal with, if
    /// any.</summary>
    internal static string? Reason(HttpContext context) =>
        context.Items.TryGetValue(ErrorHandlingMiddleware.ErrorReasonItem, out var value)
        && value is string { Length: > 0 } reason
            ? reason.Length <= MaxReasonLength ? reason : string.Concat(reason.AsSpan(0, MaxReasonLength), "...")
            : null;

    /// <summary>Classify expected protocol refusals, stale transport connections and controller
    /// resource misses as Information. Ingest-key refusals use separately deduplicated warnings
    /// while their request lines remain Debug.</summary>
    internal static bool IsExpectedRefusal(HttpContext context) =>
        (context.Response.StatusCode == StatusCodes.Status401Unauthorized && !IsIngestRoute(context))
        || IsStaleHubTransport(context)
        || IsResourceNotFound(context);

    /// <summary> An ingestion key refusal. Its warning is written, deduplicated, by
    /// <c>IngestKeyRejectionLog</c>, which every refusal of the ingest controller goes through; the
    /// request line stays in debug so an exporter retrying every few seconds does not write one
    /// warning per request. </summary>
    internal static bool IsIngestKeyRefusal(HttpContext context) =>
        context.Response.StatusCode == StatusCodes.Status401Unauthorized && IsIngestRoute(context);

    private static bool IsIngestRoute(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api/ingest", StringComparison.OrdinalIgnoreCase);

    /// <summary> A 404 answered by a controller action that the request did reach means the
    /// resource it names does not exist (a file or a commit gone from a repository, a deleted
    /// record), the ordinary answer to a stale link. A 404 that matched no endpoint is counted by
    /// <see cref="UnmatchedRequestCounter"/>, and a 404 on a hub route stays a warning (see <see
    /// cref="IsStaleHubTransport"/>), because neither comes from a controller action. </summary>
    internal static bool IsResourceNotFound(HttpContext context) =>
        context.Response.StatusCode == StatusCodes.Status404NotFound
        && context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not null;

    /// <summary> After the back restarts, a browser tab reopens its SignalR transport with the
    /// connection id the previous process handed out; the new process does not know it and answers
    /// 404, then the client negotiates a new connection. That 404 is the expected end of a stale
    /// connection, not a fault. Only a transport request carrying that id qualifies: a 404 on a
    /// hub's negotiate, or on a hub route without an id, still means a wrong route and stays a
    /// warning. </summary>
    internal static bool IsStaleHubTransport(HttpContext context) =>
        context.Response.StatusCode == StatusCodes.Status404NotFound
        && context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.Value!.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase)
        && context.Request.Query.ContainsKey("id");
}
