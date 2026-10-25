// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

public readonly record struct ParsedMetricPoint(
    string MetricName,
    double Value,
    string? Unit,
    DateTime Timestamp,
    string? AttributesJson,
    MetricKind Kind = MetricKind.Gauge);
/// <param name="ExceptionType">The <c>exception.type</c> attribute of a record logged with an exception.</param>
/// <param name="Source">The instrumentation scope that wrote the record: the logger's category.</param>
public readonly record struct ParsedLogRecord(
    DateTime Timestamp, int SeverityNumber, string? SeverityText, string Body, string? AttributesJson,
    string? ExceptionType = null, string? Source = null);
public readonly record struct ParsedError(string ExceptionType, string Message, string? TopFrame, DateTime Timestamp);

/// <summary>
/// Parses OTLP/HTTP JSON export payloads (proto3 JSON mapping) into flat series we actually store.
/// ADR-021 phases 2-4. Pure and testable; no EF, no HTTP. The protobuf parser renders its generated
/// messages through the same canonical proto3 JSON mapping so both wire encodings stay equivalent.
/// </summary>
public static class OtlpJsonParser
{
    public static IReadOnlyList<ParsedMetricPoint> ParseMetrics(JsonDocument doc)
    {
        var result = new List<ParsedMetricPoint>();
        if (!doc.RootElement.TryGetProperty("resourceMetrics", out var resourceMetrics))
            return result;

        foreach (var rm in resourceMetrics.EnumerateArray())
        {
            if (!rm.TryGetProperty("scopeMetrics", out var scopeMetrics)) continue;
            foreach (var sm in scopeMetrics.EnumerateArray())
            {
                if (!sm.TryGetProperty("metrics", out var metrics)) continue;
                foreach (var metric in metrics.EnumerateArray())
                {
                    var name = GetString(metric, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var unit = GetString(metric, "unit");

                    if (metric.TryGetProperty("gauge", out var gauge))
                        AddNumberPoints(result, name, unit, gauge, MetricKind.Gauge);
                    else if (metric.TryGetProperty("sum", out var sum))
                        AddNumberPoints(result, name, unit, sum, MetricKind.Sum);
                    else if (metric.TryGetProperty("histogram", out var histogram))
                        AddHistogramP95Points(result, name, unit, histogram);
                }
            }
        }
        return result;
    }

    private static void AddNumberPoints(
        List<ParsedMetricPoint> result, string name, string? unit, JsonElement container, MetricKind kind)
    {
        if (!container.TryGetProperty("dataPoints", out var points)) return;
        foreach (var dp in points.EnumerateArray())
        {
            double value;
            if (dp.TryGetProperty("asDouble", out var d) && d.ValueKind is JsonValueKind.Number)
                value = d.GetDouble();
            else if (dp.TryGetProperty("asInt", out var i))
                value = ReadLongLike(i);
            else
                continue;

            result.Add(new ParsedMetricPoint(
                name, value, unit, ReadUnixNano(dp, "timeUnixNano"), ReadAttributes(dp), kind));
        }
    }

    private static void AddHistogramP95Points(List<ParsedMetricPoint> result, string name, string? unit, JsonElement histogram)
    {
        if (!histogram.TryGetProperty("dataPoints", out var points)) return;
        foreach (var dp in points.EnumerateArray())
        {
            if (!TryEstimateHistogramPercentile(dp, 0.95, out var value)) continue;
            result.Add(new ParsedMetricPoint(
                name, value, unit, ReadUnixNano(dp, "timeUnixNano"), ReadAttributes(dp), MetricKind.Histogram));
        }
    }

    private static bool TryEstimateHistogramPercentile(JsonElement dataPoint, double percentile, out double value)
    {
        value = 0;
        if (!dataPoint.TryGetProperty("bucketCounts", out var countsElement)
            || countsElement.ValueKind != JsonValueKind.Array
            || !dataPoint.TryGetProperty("explicitBounds", out var boundsElement)
            || boundsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var counts = countsElement.EnumerateArray().Select(ReadLongLike).ToArray();
        var bounds = boundsElement.EnumerateArray()
            .Where(bound => bound.ValueKind == JsonValueKind.Number)
            .Select(bound => bound.GetDouble())
            .ToArray();
        if (counts.Length != bounds.Length + 1 || counts.Any(count => count < 0)) return false;

        var total = counts.Sum();
        if (total <= 0) return false;
        var rank = Math.Max(1L, (long)Math.Ceiling(total * percentile));
        long cumulative = 0;
        for (var index = 0; index < counts.Length; index++)
        {
            cumulative += counts[index];
            if (cumulative < rank) continue;

            if (index < bounds.Length)
            {
                value = bounds[index];
                return true;
            }

            if (dataPoint.TryGetProperty("max", out var max) && max.ValueKind == JsonValueKind.Number)
            {
                value = max.GetDouble();
                return true;
            }

            value = bounds.Length > 0 ? bounds[^1] : 0;
            return true;
        }

        return false;
    }

    public static IReadOnlyList<ParsedLogRecord> ParseLogs(JsonDocument doc)
    {
        var result = new List<ParsedLogRecord>();
        if (!doc.RootElement.TryGetProperty("resourceLogs", out var resourceLogs))
            return result;

        foreach (var rl in resourceLogs.EnumerateArray())
        {
            if (!rl.TryGetProperty("scopeLogs", out var scopeLogs)) continue;
            foreach (var sl in scopeLogs.EnumerateArray())
            {
                if (!sl.TryGetProperty("logRecords", out var records)) continue;
                var source = sl.TryGetProperty("scope", out var scope) ? GetString(scope, "name") : null;
                foreach (var lr in records.EnumerateArray())
                {
                    var severityNumber = lr.TryGetProperty("severityNumber", out var sn) ? ReadSeverityNumber(sn) : 0;
                    var severityText = GetString(lr, "severityText");
                    var body = lr.TryGetProperty("body", out var b) ? ReadAnyValue(b) ?? string.Empty : string.Empty;
                    var ts = ReadUnixNano(lr, "timeUnixNano");
                    if (ts == default) ts = ReadUnixNano(lr, "observedTimeUnixNano");
                    result.Add(new ParsedLogRecord(
                        ts, severityNumber, severityText, body, ReadAttributes(lr),
                        ReadAttributeDict(lr).GetValueOrDefault("exception.type"), source));
                }
            }
        }
        return result;
    }

    public static IReadOnlyList<ParsedError> ParseErrors(JsonDocument doc)
    {
        var result = new List<ParsedError>();
        if (!doc.RootElement.TryGetProperty("resourceSpans", out var resourceSpans))
            return result;

        foreach (var rs in resourceSpans.EnumerateArray())
        {
            if (!rs.TryGetProperty("scopeSpans", out var scopeSpans)) continue;
            foreach (var ss in scopeSpans.EnumerateArray())
            {
                if (!ss.TryGetProperty("spans", out var spans)) continue;
                foreach (var span in spans.EnumerateArray())
                    AddSpanErrors(result, span);
            }
        }
        return result;
    }

    private static void AddSpanErrors(ICollection<ParsedError> result, JsonElement span)
    {
        var isError = span.TryGetProperty("status", out var status)
                      && status.TryGetProperty("code", out var code)
                      && ReadStatusCode(code) == 2;
        if (!span.TryGetProperty("events", out var events))
        {
            if (isError) result.Add(CreateSpanError(span));
            return;
        }
        var hadException = false;
        foreach (var exceptionEvent in events.EnumerateArray())
        {
            if (!string.Equals(GetString(exceptionEvent, "name"), "exception", StringComparison.Ordinal)) continue;
            hadException = true;
            var attributes = ReadAttributeDict(exceptionEvent);
            var type = attributes.GetValueOrDefault("exception.type", "Exception");
            result.Add(new ParsedError(
                type, string.Empty, null, ReadUnixNano(exceptionEvent, "timeUnixNano")));
        }
        if (isError && !hadException) result.Add(CreateSpanError(span));
    }

    private static ParsedError CreateSpanError(JsonElement span) =>
        new("SpanError", GetString(span, "name") ?? "error", null, ReadUnixNano(span, "endTimeUnixNano"));

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // OTLP proto3 JSON encodes 64-bit ints as strings; tolerate both string and number.
    private static long ReadLongLike(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0,
        JsonValueKind.Number => el.TryGetInt64(out var n) ? n : (long)el.GetDouble(),
        _ => 0
    };

    private static int ReadStatusCode(JsonElement code) => code.ValueKind switch
    {
        JsonValueKind.Number => code.GetInt32(),
        JsonValueKind.String => code.GetString() == "STATUS_CODE_ERROR" ? 2 : (code.GetString() == "STATUS_CODE_OK" ? 1 : 0),
        _ => 0
    };

    // Canonical proto3 JSON renders enum values symbolically (for example
    // SEVERITY_NUMBER_ERROR), while hand-written OTLP/JSON clients may send the numeric value.
    private static int ReadSeverityNumber(JsonElement severity) => severity.ValueKind switch
    {
        JsonValueKind.Number => (int)ReadLongLike(severity),
        JsonValueKind.String => ReadSeverityNumber(severity.GetString()),
        _ => 0
    };

    private static int ReadSeverityNumber(string? severity)
    {
        if (int.TryParse(severity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            return numeric;

        const string prefix = "SEVERITY_NUMBER_";
        if (severity is null || !severity.StartsWith(prefix, StringComparison.Ordinal))
            return 0;

        var name = severity[prefix.Length..];
        var baseValue = name switch
        {
            "UNSPECIFIED" => 0,
            var value when value.StartsWith("TRACE", StringComparison.Ordinal) => 1,
            var value when value.StartsWith("DEBUG", StringComparison.Ordinal) => 5,
            var value when value.StartsWith("INFO", StringComparison.Ordinal) => 9,
            var value when value.StartsWith("WARN", StringComparison.Ordinal) => 13,
            var value when value.StartsWith("ERROR", StringComparison.Ordinal) => 17,
            var value when value.StartsWith("FATAL", StringComparison.Ordinal) => 21,
            _ => 0
        };

        var variant = name.Length > 0 && name[^1] is >= '2' and <= '4' ? name[^1] - '1' : 0;
        return baseValue + variant;
    }

    private static DateTime ReadUnixNano(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return default;
        var nanos = ReadLongLike(v);
        if (nanos <= 0) return default;
        return DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000).UtcDateTime;
    }

    private static string? ReadAnyValue(JsonElement value)
    {
        if (value.TryGetProperty("stringValue", out var s)) return s.GetString();
        if (value.TryGetProperty("intValue", out var i)) return ReadLongLike(i).ToString(CultureInfo.InvariantCulture);
        if (value.TryGetProperty("doubleValue", out var d) && d.ValueKind is JsonValueKind.Number) return d.GetDouble().ToString(CultureInfo.InvariantCulture);
        if (value.TryGetProperty("boolValue", out var b)) return b.GetBoolean() ? "true" : "false";
        return null;
    }

    private static Dictionary<string, string> ReadAttributeDict(JsonElement owner)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!owner.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array)
            return dict;
        foreach (var a in attrs.EnumerateArray())
        {
            var key = GetString(a, "key");
            if (key is null || !a.TryGetProperty("value", out var val)) continue;
            var v = ReadAnyValue(val);
            if (v is not null) dict[key] = v;
        }
        return dict;
    }

    private static string? ReadAttributes(JsonElement owner)
    {
        var dict = ReadAttributeDict(owner);
        if (dict.Count == 0) return null;
        var json = JsonSerializer.Serialize(dict);
        return json.Length > 1024 ? json[..1024] : json;
    }
}
