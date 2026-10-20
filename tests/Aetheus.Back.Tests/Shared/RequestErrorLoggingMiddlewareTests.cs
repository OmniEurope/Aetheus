// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

public sealed class RequestErrorLoggingMiddlewareTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 28, 3, 0, 10, TimeSpan.Zero));
    private readonly RecordingLogger<UnmatchedRequestCounter> _counterLog = new();

    private UnmatchedRequestCounter Counter() => new(_clock, _counterLog);

    private static DefaultHttpContext Context(int status, bool matchedRoute, string path = "/api/servers")
    {
        var context = new DefaultHttpContext { TraceIdentifier = "request-42" };
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = status;
        if (matchedRoute)
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "route"));
        return context;
    }

    private static RequestErrorLoggingMiddleware Middleware(
        ILogger<RequestErrorLoggingMiddleware> logger, UnmatchedRequestCounter counter, int status) =>
        new(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }, logger, counter);

    [Fact]
    public async Task InvokeAsync_ErrorResponse_AddsCorrelationHeaderAndLogs()
    {
        var logger = Substitute.For<ILogger<RequestErrorLoggingMiddleware>>();
        var context = Context(StatusCodes.Status400BadRequest, matchedRoute: true);

        await Middleware(logger, Counter(), StatusCodes.Status400BadRequest).InvokeAsync(context);
        await context.Response.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("request-42", context.Response.Headers[RequestErrorLoggingMiddleware.CorrelationHeader]);
        logger.ReceivedWithAnyArgs(1).Log(
            default,
            default,
            default!,
            null,
            default!);
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    public async Task R457_ARefusedRequestToNoRoute_IsCounted_NotLogged(int status)
    {
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();

        await Middleware(logger, Counter(), status).InvokeAsync(Context(status, matchedRoute: false, "/.env"));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task R457_AServerErrorWithoutRoute_IsStillAnError()
    {
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();

        await Middleware(logger, Counter(), 500).InvokeAsync(Context(500, matchedRoute: false, "/.env"));

        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task R464_TheReasonOfARefusal_IsWrittenInTheWarning()
    {
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();
        var context = Context(400, matchedRoute: true, "/api/pipelines/templates/3/resolve");
        context.Items[ErrorHandlingMiddleware.ErrorReasonItem] = "Template parameter 'env' is required.";

        await Middleware(logger, Counter(), 400).InvokeAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Template parameter 'env' is required.", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/git/1/aetheus.git/info/refs")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/settings")]
    [InlineData("/health")]
    public async Task R489_AnUnauthenticatedRequestToARealRoute_IsInformation_NotAWarning(string path)
    {
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();
        var context = Context(StatusCodes.Status401Unauthorized, matchedRoute: true, path);

        await Middleware(logger, Counter(), StatusCodes.Status401Unauthorized).InvokeAsync(context);

        Assert.Equal(LogLevel.Information, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task R489_AForbiddenRequest_StaysAWarning()
    {
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();
        var context = Context(StatusCodes.Status403Forbidden, matchedRoute: true, "/api/servers/5");

        await Middleware(logger, Counter(), StatusCodes.Status403Forbidden).InvokeAsync(context);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Theory]
    [InlineData("/api/ingest/otlp/v1/metrics")]
    [InlineData("/api/ingest/otlp/v1/logs")]
    [InlineData("/api/ingest/visitors")]
    public async Task R2_013_AnIngestKeyRefusal_IsNotAnExpectedRefusal_ItsLineIsDebug(string path)
    {
        // IngestKeyRejectionLog emits one warning per key and period for invalid ingestion
        // credentials.
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();
        var context = Context(StatusCodes.Status401Unauthorized, matchedRoute: true, path);

        await Middleware(logger, Counter(), StatusCodes.Status401Unauthorized).InvokeAsync(context);

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
        Assert.False(RequestErrorLoggingMiddleware.IsExpectedRefusal(context));
    }

    [Fact]
    public async Task R2_019_A404FromAMatchedControllerAction_IsInformation()
    {
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();
        var context = Context(StatusCodes.Status404NotFound, matchedRoute: false, "/api/git/repos/1/blob");
        context.SetEndpoint(ControllerActionEndpoint());

        await Middleware(logger, Counter(), StatusCodes.Status404NotFound).InvokeAsync(context);

        Assert.Equal(LogLevel.Information, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task R2_019_A403FromAMatchedControllerAction_StaysAWarning()
    {
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();
        var context = Context(StatusCodes.Status403Forbidden, matchedRoute: false, "/api/git/repos/1/blob");
        context.SetEndpoint(ControllerActionEndpoint());

        await Middleware(logger, Counter(), StatusCodes.Status403Forbidden).InvokeAsync(context);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Theory]
    [InlineData("/hubs/servers/negotiate")]
    [InlineData("/hubs/servers")]
    public async Task R2_019_A404OnAHubRoute_StaysAWarning(string path)
    {
        // A hub endpoint is matched but is no controller action: a 404 there is a wrong route.
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();
        var context = Context(StatusCodes.Status404NotFound, matchedRoute: true, path);

        await Middleware(logger, Counter(), StatusCodes.Status404NotFound).InvokeAsync(context);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task R2_019_A404ThatMatchedNoRoute_IsStillOnlyCounted()
    {
        var logger = new RecordingLogger<RequestErrorLoggingMiddleware>();

        await Middleware(logger, Counter(), StatusCodes.Status404NotFound)
            .InvokeAsync(Context(StatusCodes.Status404NotFound, matchedRoute: false, "/api/git/repos/x/blob"));

        Assert.Empty(logger.Entries);
    }

    private static Endpoint ControllerActionEndpoint() =>
        new(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()),
            "GitLight.GetBlob");

    [Fact]
    public void R464_ALongReason_IsCut()
    {
        var context = Context(400, matchedRoute: true);
        context.Items[ErrorHandlingMiddleware.ErrorReasonItem] = new string('x', 1000);

        var reason = RequestErrorLoggingMiddleware.Reason(context);

        Assert.Equal(RequestErrorLoggingMiddleware.MaxReasonLength + 3, reason!.Length);
        Assert.EndsWith("...", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void R457_TheCounter_WritesOneLinePerMinute_AWarningFromThePeakOn()
    {
        var counter = Counter();

        for (var i = 0; i < 3; i++) counter.Record();
        Assert.Empty(_counterLog.Entries);            // the minute is still open
        _clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < UnmatchedRequestCounter.PeakPerMinute; i++) counter.Record();
        _clock.Advance(TimeSpan.FromMinutes(1));
        counter.Record();

        Assert.Equal(2, _counterLog.Entries.Count);
        Assert.Equal(LogLevel.Information, _counterLog.Entries[0].Level);
        Assert.StartsWith("3 refused requests", _counterLog.Entries[0].Message, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, _counterLog.Entries[1].Level);
        Assert.StartsWith($"{UnmatchedRequestCounter.PeakPerMinute} refused requests", _counterLog.Entries[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/hubs/servers", "?id=abc", 404, true)]
    [InlineData("/hubs/pipelines", "?id=abc", 404, true)]
    [InlineData("/hubs/servers/negotiate", "?id=abc", 404, false)]
    [InlineData("/hubs/servers", "", 404, false)]
    [InlineData("/api/servers", "?id=abc", 404, false)]
    [InlineData("/hubs/servers", "?id=abc", 401, false)]
    public void StaleHubTransport_IsOnlyATransport404CarryingAConnectionId(string path, string query, int status, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.StatusCode = status;

        Assert.Equal(expected, RequestErrorLoggingMiddleware.IsStaleHubTransport(context));
    }
}
