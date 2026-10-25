// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Back.Data.Entities;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// Projects the generated OTLP protobuf messages directly into the bounded persistence read models.
/// This avoids the previous protobuf -> JSON string -> JsonDocument intermediate representation.
/// </summary>
public static class OtlpProtobufParser
{
    private const int MaxAttributesJsonLength = 1024;
    private const string TruncatedAttributesKey = "_aetheus_truncated";

    public static IReadOnlyList<ParsedMetricPoint> ParseMetrics(byte[] payload)
    {
        var request = ExportMetricsServiceRequest.Parser.ParseFrom(payload);
        var result = new List<ParsedMetricPoint>();
        foreach (var resource in request.ResourceMetrics)
            foreach (var scope in resource.ScopeMetrics)
                foreach (var metric in scope.Metrics)
                {
                    if (string.IsNullOrWhiteSpace(metric.Name))
                        continue;
                    if (metric.Gauge is not null)
                        AddNumberPoints(result, metric.Name, metric.Unit, metric.Gauge.DataPoints, MetricKind.Gauge);
                    else if (metric.Sum is not null)
                        AddNumberPoints(result, metric.Name, metric.Unit, metric.Sum.DataPoints, MetricKind.Sum);
                    else if (metric.Histogram is not null)
                        AddHistogramPoints(result, metric.Name, metric.Unit, metric.Histogram.DataPoints);
                }
        return result;
    }

    public static IReadOnlyList<ParsedLogRecord> ParseLogs(byte[] payload)
    {
        var request = ExportLogsServiceRequest.Parser.ParseFrom(payload);
        var result = new List<ParsedLogRecord>();
        foreach (var resource in request.ResourceLogs)
            foreach (var scope in resource.ScopeLogs)
                foreach (var record in scope.LogRecords)
                {
                    var timestamp = ReadUnixNano(record.TimeUnixNano);
                    if (timestamp == default)
                        timestamp = ReadUnixNano(record.ObservedTimeUnixNano);
                    result.Add(new ParsedLogRecord(
                        timestamp,
                        (int)record.SeverityNumber,
                        EmptyToNull(record.SeverityText),
                        ReadAnyValue(record.Body) ?? string.Empty,
                        ReadAttributes(record.Attributes),
                        ReadAttributeDictionary(record.Attributes).GetValueOrDefault("exception.type"),
                        EmptyToNull(scope.Scope?.Name)));
                }
        return result;
    }

    public static IReadOnlyList<ParsedError> ParseErrors(byte[] payload)
    {
        var request = ExportTraceServiceRequest.Parser.ParseFrom(payload);
        var result = new List<ParsedError>();
        foreach (var resource in request.ResourceSpans)
            foreach (var scope in resource.ScopeSpans)
                foreach (var span in scope.Spans)
                {
                    var isError = span.Status?.Code == Status.Types.StatusCode.Error;
                    var hadException = false;
                    foreach (var spanEvent in span.Events)
                    {
                        if (!string.Equals(spanEvent.Name, "exception", StringComparison.Ordinal))
                            continue;
                        hadException = true;
                        var attributes = ReadAttributeDictionary(spanEvent.Attributes);
                        result.Add(new ParsedError(
                            attributes.GetValueOrDefault("exception.type", "Exception"),
                            string.Empty,
                            null,
                            ReadUnixNano(spanEvent.TimeUnixNano)));
                    }

                    if (isError && !hadException)
                        result.Add(new ParsedError(
                            "SpanError",
                            string.IsNullOrWhiteSpace(span.Name) ? "error" : span.Name,
                            null,
                            ReadUnixNano(span.EndTimeUnixNano)));
                }
        return result;
    }

    private static void AddNumberPoints(
        List<ParsedMetricPoint> result,
        string name,
        string? unit,
        RepeatedField<NumberDataPoint> points,
        MetricKind kind)
    {
        foreach (var point in points)
        {
            double value;
            switch (point.ValueCase)
            {
                case NumberDataPoint.ValueOneofCase.AsDouble:
                    value = point.AsDouble;
                    break;
                case NumberDataPoint.ValueOneofCase.AsInt:
                    value = point.AsInt;
                    break;
                default:
                    continue;
            }
            result.Add(new ParsedMetricPoint(
                name,
                value,
                EmptyToNull(unit),
                ReadUnixNano(point.TimeUnixNano),
                ReadAttributes(point.Attributes),
                kind));
        }
    }

    private static void AddHistogramPoints(
        List<ParsedMetricPoint> result,
        string name,
        string? unit,
        RepeatedField<HistogramDataPoint> points)
    {
        foreach (var point in points)
        {
            if (!TryEstimateHistogramP95(point, out var value))
                continue;
            result.Add(new ParsedMetricPoint(
                name,
                value,
                EmptyToNull(unit),
                ReadUnixNano(point.TimeUnixNano),
                ReadAttributes(point.Attributes),
                MetricKind.Histogram));
        }
    }

    private static bool TryEstimateHistogramP95(HistogramDataPoint point, out double value)
    {
        value = 0;
        if (point.BucketCounts.Count != point.ExplicitBounds.Count + 1)
            return false;
        ulong total = 0;
        foreach (var count in point.BucketCounts)
        {
            if (ulong.MaxValue - total < count)
                return false;
            total += count;
        }
        if (total == 0)
            return false;
        var rank = Math.Max(1UL, (ulong)Math.Ceiling(total * 0.95));
        ulong cumulative = 0;
        for (var index = 0; index < point.BucketCounts.Count; index++)
        {
            cumulative += point.BucketCounts[index];
            if (cumulative < rank)
                continue;
            if (index < point.ExplicitBounds.Count)
                value = point.ExplicitBounds[index];
            else if (point.HasMax)
                value = point.Max;
            else
                value = point.ExplicitBounds.Count > 0 ? point.ExplicitBounds[^1] : 0;
            return true;
        }
        return false;
    }

    private static DateTime ReadUnixNano(ulong nanos)
    {
        if (nanos == 0 || nanos / 1_000_000 > long.MaxValue)
            return default;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(nanos / 1_000_000)).UtcDateTime;
    }

    private static string? ReadAttributes(RepeatedField<KeyValue> attributes)
    {
        var dictionary = ReadAttributeDictionary(attributes);
        if (dictionary.Count == 0)
            return null;
        var json = JsonSerializer.Serialize(dictionary);
        if (json.Length <= MaxAttributesJsonLength)
            return json;

        var bounded = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TruncatedAttributesKey] = bool.TrueString
        };
        foreach (var attribute in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            bounded[attribute.Key] = attribute.Value;
            var candidate = JsonSerializer.Serialize(bounded);
            if (candidate.Length <= MaxAttributesJsonLength)
                continue;
            bounded.Remove(attribute.Key);
            break;
        }
        return JsonSerializer.Serialize(bounded);
    }

    private static Dictionary<string, string> ReadAttributeDictionary(RepeatedField<KeyValue> attributes)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            var value = ReadAnyValue(attribute.Value);
            if (!string.IsNullOrEmpty(attribute.Key) && value is not null)
                dictionary[attribute.Key] = value;
        }
        return dictionary;
    }

    private static string? ReadAnyValue(AnyValue? value)
    {
        if (value is null)
            return null;
        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(CultureInfo.InvariantCulture),
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => null
        };
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
