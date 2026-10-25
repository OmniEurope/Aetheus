// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.AppMonitoring;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using NSubstitute;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>
/// Unit coverage for the public OTLP ingest surface (audit F-MON-02). 401 / 415 / 400 are enforced inside
/// the action, so they are exercised directly here; 413 (RequestSizeLimit) and 429 (rate limiter) are
/// enforced by middleware and asserted structurally (the attributes are present and wired).
/// </summary>
public class IngestControllerTests
{
    private readonly IIngestService _ingest = Substitute.For<IIngestService>();
    private readonly IVisitorIngestService _visitorIngest = Substitute.For<IVisitorIngestService>();
    private readonly IAppWebAnalyticsService _webAnalytics = Substitute.For<IAppWebAnalyticsService>();
    private readonly IIngestConfigurationService _configuration = Substitute.For<IIngestConfigurationService>();

    private IngestController Build(string? key, string? contentType, string body) =>
        Build(key, contentType, Encoding.UTF8.GetBytes(body));

    private IngestController Build(string? key, string? contentType, byte[] body)
    {
        var ctx = new DefaultHttpContext();
        if (key is not null) ctx.Request.Headers["x-aetheus-ingest-key"] = key;
        if (contentType is not null) ctx.Request.ContentType = contentType;
        ctx.Request.Body = new MemoryStream(body);
        return new IngestController(_ingest, _visitorIngest, _webAnalytics, _configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };
    }

    [Fact]
    public async Task Metrics_MissingKey_ReturnsUnauthorized()
    {
        var controller = Build(key: null, contentType: "application/json", body: "{}");
        Assert.IsType<UnauthorizedResult>(await controller.Metrics(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Metrics_UnknownKey_ReturnsUnauthorized()
    {
        _ingest.ResolveAppIdAsync("bad", Arg.Any<CancellationToken>()).Returns((int?)null);
        var controller = Build(key: "bad", contentType: "application/json", body: "{}");
        Assert.IsType<UnauthorizedResult>(await controller.Metrics(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EveryExporterEndpoint_RefusesAnUnknownKeyWith401_NeverWithAServerError()
    {
        // R1-11: an exporter reads 401 as permanent and stops sending that key; a 5xx would be
        // retried forever. Each endpoint an Aetheus exporter posts to answers a refused key with 401.
        _ingest.ResolveAppIdAsync("stale", Arg.Any<CancellationToken>()).Returns((int?)null);
        var ct = TestContext.Current.CancellationToken;

        Assert.IsType<UnauthorizedResult>(await Build("stale", "application/x-protobuf", "").Metrics(ct));
        Assert.IsType<UnauthorizedResult>(await Build("stale", "application/x-protobuf", "").Logs(ct));
        Assert.IsType<UnauthorizedResult>(await Build("stale", "application/x-protobuf", "").Traces(ct));
        Assert.IsType<UnauthorizedResult>(await Build("stale", "application/json", "[]").WebAnalytics([], ct));
    }

    [Fact]
    public async Task Metrics_UnsupportedContentType_Returns415()
    {
        _ingest.ResolveAppIdAsync("ok", Arg.Any<CancellationToken>()).Returns(1);
        var controller = Build(key: "ok", contentType: "text/plain", body: "{}");

        var result = await controller.Metrics(TestContext.Current.CancellationToken);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, status.StatusCode);
    }

    [Fact]
    public async Task Metrics_ValidProtobuf_IngestsPoint_AndReturnsProtobufResponse()
    {
        _ingest.ResolveAppIdAsync("ok", Arg.Any<CancellationToken>()).Returns(1);
        _ingest.IngestMetricsAsync(1, Arg.Any<IReadOnlyList<ParsedMetricPoint>>(), Arg.Any<CancellationToken>())
            .Returns(new IngestOutcome(1, 0));
        var request = new ExportMetricsServiceRequest
        {
            ResourceMetrics =
            {
                new ResourceMetrics
                {
                    ScopeMetrics =
                    {
                        new ScopeMetrics
                        {
                            Metrics =
                            {
                                new Metric
                                {
                                    Name = "http.server.request.duration",
                                    Unit = "s",
                                    Gauge = new Gauge
                                    {
                                        DataPoints =
                                        {
                                            new NumberDataPoint { AsDouble = 0.25, TimeUnixNano = 1_767_873_600_000_000_000 }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
        var controller = Build("ok", "application/x-protobuf", request.ToByteArray());

        var result = Assert.IsType<FileContentResult>(await controller.Metrics(TestContext.Current.CancellationToken));

        Assert.Equal("application/x-protobuf", result.ContentType);
        var response = ExportMetricsServiceResponse.Parser.ParseFrom(result.FileContents);
        Assert.Equal(0, response.PartialSuccess?.RejectedDataPoints ?? 0);
        await _ingest.Received(1).IngestMetricsAsync(1,
            Arg.Is<IReadOnlyList<ParsedMetricPoint>>(points => points.Count == 1
                && points[0].MetricName == "http.server.request.duration"
                && points[0].Value == 0.25), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Metrics_MalformedProtobuf_ReturnsBadRequest()
    {
        _ingest.ResolveAppIdAsync("ok", Arg.Any<CancellationToken>()).Returns(1);
        var controller = Build("ok", "application/x-protobuf", [0xFF]);

        Assert.IsType<BadRequestObjectResult>(await controller.Metrics(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Metrics_MalformedJson_ReturnsBadRequest()
    {
        _ingest.ResolveAppIdAsync("ok", Arg.Any<CancellationToken>()).Returns(1);
        var controller = Build(key: "ok", contentType: "application/json", body: "not-json");

        Assert.IsType<BadRequestObjectResult>(await controller.Metrics(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Metrics_ValidKeyAndJson_ReturnsOk_AndIngests()
    {
        _ingest.ResolveAppIdAsync("ok", Arg.Any<CancellationToken>()).Returns(1);
        _ingest.IngestMetricsAsync(1, Arg.Any<IReadOnlyList<ParsedMetricPoint>>(), Arg.Any<CancellationToken>())
            .Returns(new IngestOutcome(0, 0));
        var controller = Build(key: "ok", contentType: "application/json", body: "{}");

        Assert.IsType<OkObjectResult>(await controller.Metrics(TestContext.Current.CancellationToken));
        await _ingest.Received(1).IngestMetricsAsync(1, Arg.Any<IReadOnlyList<ParsedMetricPoint>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Visitor_RequiresIngestKey_ThenDelegatesOpaqueDigest()
    {
        var request = new Aetheus.Shared.Components.AppMonitoring.AppVisitorIngestRequest { VisitorId = new string('a', 64) };
        Assert.IsType<UnauthorizedResult>(await Build(null, null, "").Visitor(
            request, TestContext.Current.CancellationToken));

        _ingest.ResolveAppIdAsync("ok", Arg.Any<CancellationToken>()).Returns(7);
        var result = await Build("ok", null, "").Visitor(request, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await _visitorIngest.Received(1).RecordAsync(7, request.VisitorId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Controller_DeclaresBodySizeCap_AndRateLimit()
    {
        var attrs = typeof(IngestController).GetCustomAttributes(inherit: true);
        Assert.Single(attrs.OfType<RequestSizeLimitAttribute>()); // 413 for bodies over the 1 MiB cap
        var rateLimit = Assert.Single(attrs.OfType<EnableRateLimitingAttribute>());
        Assert.Equal("otlp-ingest", rateLimit.PolicyName); // 429 when the per-key limiter trips
    }

    [Fact]
    public async Task Configuration_UnknownKey_Returns401()
    {
        _ingest.ResolveAppIdAsync("bad", Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await Build("bad", null, string.Empty).Configuration(TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task Configuration_ReturnsTheSendIntervalOfTheKeysApplication()
    {
        _ingest.ResolveAppIdAsync("ok", Arg.Any<CancellationToken>()).Returns(7);
        _configuration.GetAsync(7, Arg.Any<CancellationToken>()).Returns(new IngestConfigurationDto(900));

        var result = await Build("ok", null, string.Empty).Configuration(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(900, Assert.IsType<IngestConfigurationDto>(ok.Value).MetricExportIntervalSeconds);
    }
}
