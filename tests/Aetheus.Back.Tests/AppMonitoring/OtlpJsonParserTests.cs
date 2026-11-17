// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.AppMonitoring;

public class OtlpJsonParserTests
{
    [Fact]
    public void ParseMetrics_HandlesGauge_Sum_And_HistogramP95()
    {
        const string json = """
        {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
          {"name":"http.server.active_requests","unit":"{requests}","gauge":{"dataPoints":[
            {"timeUnixNano":"1700000000000000000","asInt":"3","attributes":[{"key":"route","value":{"stringValue":"/api"}}]}]}},
          {"name":"http.server.request.count","sum":{"dataPoints":[
            {"timeUnixNano":"1700000000000000000","asDouble":42.0}]}},
          {"name":"http.server.request.duration","unit":"s","histogram":{"dataPoints":[
            {"timeUnixNano":"1700000000000000000","count":"100","sum":55.0,
             "explicitBounds":[0.1,1.0,10.0],"bucketCounts":["90","5","4","1"],"max":20.0}]}}
        ]}]}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var points = OtlpJsonParser.ParseMetrics(doc);

        Assert.Equal(3, points.Count);
        var gauge = points.Single(p => p.MetricName == "http.server.active_requests");
        Assert.Equal(3, gauge.Value);
        Assert.Equal("{requests}", gauge.Unit);
        Assert.Contains("route", gauge.AttributesJson);
        Assert.Equal(42.0, points.Single(p => p.MetricName == "http.server.request.count").Value);
        var duration = points.Single(p => p.MetricName == "http.server.request.duration").Value;
        Assert.Equal(1.0, duration); // nearest-rank P95 reaches the second bucket upper bound
        Assert.NotEqual(0.55, duration); // never regress to the misleading sum/count mean
        Assert.NotEqual(default, gauge.Timestamp);
    }

    [Fact]
    public void R491_ParseLogs_KeepsTheExceptionTypeAndTheLoggerOfARecord()
    {
        const string json = """
        {"resourceLogs":[{"scopeLogs":[{"scope":{"name":"Aetheus.Back.Services.AppTelemetryRetentionService"},"logRecords":[
          {"timeUnixNano":"1700000000000000000","severityNumber":17,"body":{"stringValue":"sweep failed"},
           "attributes":[{"key":"exception.type","value":{"stringValue":"Npgsql.PostgresException"}}]},
          {"timeUnixNano":"1700000000000000000","severityNumber":9,"body":{"stringValue":"sweep done"}}
        ]}]}]}
        """;
        using var doc = JsonDocument.Parse(json);

        var logs = OtlpJsonParser.ParseLogs(doc);

        Assert.Equal("Npgsql.PostgresException", logs[0].ExceptionType);
        Assert.Equal("Aetheus.Back.Services.AppTelemetryRetentionService", logs[0].Source);
        Assert.Null(logs[1].ExceptionType);
    }

    [Fact]
    public void ParseLogs_ExtractsSeverityAndBody()
    {
        const string json = """
        {"resourceLogs":[{"scopeLogs":[{"logRecords":[
          {"timeUnixNano":"1700000000000000000","severityNumber":17,"severityText":"ERROR",
           "body":{"stringValue":"boom"},"attributes":[{"key":"k","value":{"stringValue":"v"}}]}
        ]}]}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var logs = OtlpJsonParser.ParseLogs(doc);

        var log = Assert.Single(logs);
        Assert.Equal(17, log.SeverityNumber);
        Assert.Equal("ERROR", log.SeverityText);
        Assert.Equal("boom", log.Body);
        Assert.Contains("k", log.AttributesJson);
    }

    [Theory]
    [InlineData("SEVERITY_NUMBER_TRACE", 1)]
    [InlineData("SEVERITY_NUMBER_DEBUG3", 7)]
    [InlineData("SEVERITY_NUMBER_INFO4", 12)]
    [InlineData("SEVERITY_NUMBER_WARN", 13)]
    [InlineData("SEVERITY_NUMBER_ERROR", 17)]
    [InlineData("SEVERITY_NUMBER_FATAL4", 24)]
    public void ParseLogs_MapsCanonicalProtobufSeverityNames(string severity, int expected)
    {
        var json = """
        {"resourceLogs":[{"scopeLogs":[{"logRecords":[
          {"severityNumber":"__SEVERITY__","body":{"stringValue":"event"}}
        ]}]}]}
        """.Replace("__SEVERITY__", severity, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);

        var log = Assert.Single(OtlpJsonParser.ParseLogs(doc));

        Assert.Equal(expected, log.SeverityNumber);
    }

    [Fact]
    public void ParseErrors_DropsSensitiveExceptionDetailsAndKeepsType()
    {
        const string json = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[
          {"name":"GET /x","status":{"code":2},"events":[
            {"name":"exception","timeUnixNano":"1700000000000000000","attributes":[
              {"key":"exception.type","value":{"stringValue":"System.InvalidOperationException"}},
              {"key":"exception.message","value":{"stringValue":"bad state"}},
              {"key":"exception.stacktrace","value":{"stringValue":"at Foo.Bar()\n at Baz.Qux()"}}]}]}
        ]}]}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var errors = OtlpJsonParser.ParseErrors(doc);

        var err = Assert.Single(errors);
        Assert.Equal("System.InvalidOperationException", err.ExceptionType);
        Assert.True(string.IsNullOrEmpty(err.Message));
        Assert.True(string.IsNullOrEmpty(err.TopFrame));
    }

    [Fact]
    public void ParseErrors_ErrorSpanWithoutExceptionEvent_StillReported()
    {
        const string json = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[
          {"name":"GET /y","endTimeUnixNano":"1700000000000000000","status":{"code":"STATUS_CODE_ERROR"}}
        ]}]}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var errors = OtlpJsonParser.ParseErrors(doc);
        Assert.Single(errors);
    }

    [Fact]
    public void ParseMetrics_EmptyOrUnknownShape_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"foo":"bar"}""");
        Assert.Empty(OtlpJsonParser.ParseMetrics(doc));
    }

    [Theory]
    [InlineData("\"explicitBounds\":[10,20],\"bucketCounts\":[5,90,5]", 20.0)]
    [InlineData("\"explicitBounds\":[10,20],\"bucketCounts\":[\"0\",\"0\",\"10\"],\"max\":42.5", 42.5)]
    [InlineData("\"explicitBounds\":[10],\"bucketCounts\":[0,10],\"max\":\"n/a\"", 10.0)]
    [InlineData("\"explicitBounds\":[],\"bucketCounts\":[3]", 0.0)]
    [InlineData("\"explicitBounds\":[10],\"bucketCounts\":[1]", null)]
    [InlineData("\"explicitBounds\":[10],\"bucketCounts\":[-1,5]", null)]
    [InlineData("\"explicitBounds\":[10],\"bucketCounts\":[0,0]", null)]
    [InlineData("\"explicitBounds\":10,\"bucketCounts\":[1,1]", null)]
    [InlineData("\"bucketCounts\":[1]", null)]
    [InlineData("\"explicitBounds\":[],\"bucketCounts\":\"1\"", null)]
    public void ParseMetrics_HistogramP95_UsesTheRankBucketAndRejectsMalformedPoints(string point, double? expected)
    {
        using var doc = JsonDocument.Parse(
            "{\"resourceMetrics\":[{\"scopeMetrics\":[{\"metrics\":[{\"name\":\"latency\",\"histogram\":{\"dataPoints\":[{"
            + point + "}]}}]}]}]}");

        var points = OtlpJsonParser.ParseMetrics(doc);

        if (expected is null)
        {
            Assert.Empty(points);
            return;
        }
        var parsed = Assert.Single(points);
        Assert.Equal(expected.Value, parsed.Value);
        Assert.Equal(MetricKind.Histogram, parsed.Kind);
    }

    [Theory]
    [InlineData("{\"stringValue\":\"text\"}", "text")]
    [InlineData("{\"intValue\":\"42\"}", "42")]
    [InlineData("{\"intValue\":7}", "7")]
    [InlineData("{\"doubleValue\":1.5}", "1.5")]
    [InlineData("{\"boolValue\":true}", "true")]
    [InlineData("{\"boolValue\":false}", "false")]
    [InlineData("{\"doubleValue\":\"1.5\"}", "")]
    [InlineData("{\"arrayValue\":{}}", "")]
    public void ParseLogs_ReadsEveryScalarBodyKind(string body, string expected)
    {
        using var doc = JsonDocument.Parse(
            "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"body\":" + body
            + ",\"attributes\":[{\"key\":\"attempt\",\"value\":" + body + "}]}]}]}]}");

        var log = Assert.Single(OtlpJsonParser.ParseLogs(doc));

        Assert.Equal(expected, log.Body);
        if (expected.Length == 0)
            Assert.Null(log.AttributesJson);
        else
            Assert.Equal(expected, JsonDocument.Parse(log.AttributesJson!).RootElement.GetProperty("attempt").GetString());
    }

    /// <summary>BACK-005: an attribute set over the 1024-character column used to be cut mid-text, so the
    /// performance table dropped the route as malformed. It is now bounded structurally: valid JSON, within
    /// the column, the route and method kept, and the route reaches the performance report.</summary>
    [Fact]
    public void ParseMetrics_OversizedAttributes_StayValidJson_AndTheRouteSurvivesToThePerformanceReport()
    {
        var filler = string.Join(",", Enumerable.Range(0, 12).Select(index =>
            "{\"key\":\"a.filler." + index + "\",\"value\":{\"stringValue\":\"" + new string('x', 150) + "\"}}"));
        var json = """
        {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
          {"name":"aetheus.http.server.request.duration.p95","unit":"ms","gauge":{"dataPoints":[
            {"timeUnixNano":"1700000000000000000","asDouble":12.5,"attributes":[__FILLER__,
              {"key":"http.route","value":{"stringValue":"api/projects/{id}"}},
              {"key":"http.request.method","value":{"stringValue":"GET"}}]}]}}
        ]}]}]}
        """.Replace("__FILLER__", filler, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);

        var point = Assert.Single(OtlpJsonParser.ParseMetrics(doc));

        Assert.NotNull(point.AttributesJson);
        Assert.InRange(point.AttributesJson.Length, 1, 1024);
        using var attributes = JsonDocument.Parse(point.AttributesJson);
        Assert.Equal("api/projects/{id}", attributes.RootElement.GetProperty("http.route").GetString());
        Assert.Equal("GET", attributes.RootElement.GetProperty("http.request.method").GetString());
        Assert.Equal(bool.TrueString, attributes.RootElement.GetProperty("_aetheus_truncated").GetString());

        var report = AppRoutePerformance.Build(
        [
            new AppMetricSample
            {
                MetricName = point.MetricName,
                Value = point.Value,
                Timestamp = point.Timestamp,
                AttributesJson = point.AttributesJson
            }
        ]);
        var route = Assert.Single(report.Routes);
        Assert.Equal("api/projects/{id}", route.Route);
        Assert.Equal("GET", route.Method);
        Assert.Equal(12.5, route.P95Ms);
    }

    /// <summary>A set that fits is stored exactly as before: the hourly rollups key on this text.</summary>
    [Fact]
    public void ParseMetrics_AttributesThatFit_AreStoredUnchanged()
    {
        const string json = """
        {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
          {"name":"g","gauge":{"dataPoints":[{"timeUnixNano":"1700000000000000000","asDouble":1,
            "attributes":[{"key":"z","value":{"stringValue":"1"}},{"key":"a","value":{"stringValue":"2"}}]}]}}
        ]}]}]}
        """;
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("""{"z":"1","a":"2"}""", Assert.Single(OtlpJsonParser.ParseMetrics(doc)).AttributesJson);
    }

    /// <summary>A single value longer than the whole column is shortened rather than dropped.</summary>
    [Fact]
    public void ParseLogs_OneHugeValue_IsShortened_AndTheSetStaysValid()
    {
        var json = "{\"resourceLogs\":[{\"scopeLogs\":[{\"logRecords\":[{\"body\":{\"stringValue\":\"b\"},"
            + "\"attributes\":[{\"key\":\"huge\",\"value\":{\"stringValue\":\"" + new string('y', 5000) + "\"}}]}]}]}]}";
        using var doc = JsonDocument.Parse(json);

        var log = Assert.Single(OtlpJsonParser.ParseLogs(doc));

        Assert.InRange(log.AttributesJson!.Length, 1, 1024);
        using var attributes = JsonDocument.Parse(log.AttributesJson);
        Assert.Equal(new string('y', 256), attributes.RootElement.GetProperty("huge").GetString());
    }
}
