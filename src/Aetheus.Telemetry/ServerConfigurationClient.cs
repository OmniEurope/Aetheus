// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aetheus.Telemetry;

/// <summary> Reads the configuration Aetheus keeps for the application's ingest key (<c>GET
/// {metrics endpoint}/../configuration</c>, same key header as the exports). Anything but a valid
/// answer (an older Aetheus answers 404, a network failure, a malformed body) returns null: the
/// package keeps sending at the interval it already has. </summary>
internal sealed class ServerConfigurationClient(HttpClient http, Uri configurationUri, string ingestKey)
{
    internal const string KeyHeader = "x-aetheus-ingest-key";

    /// <summary>The configuration endpoint next to the metrics endpoint.</summary>
    internal static Uri ConfigurationUriFor(Uri metricsEndpoint) => new(metricsEndpoint, "configuration");

    internal async Task<int?> ReadMetricExportIntervalSecondsAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, configurationUri);
        request.Headers.TryAddWithoutValidation(KeyHeader, ingestKey);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await response.Content.ReadFromJsonAsync<ConfigurationBody>(ct).ConfigureAwait(false);
            return body?.MetricExportIntervalSeconds is > 0 ? body.MetricExportIntervalSeconds : null;
        }
        // Best effort by design: the configuration only tunes the interval, it must never stop the
        // application nor its exports.
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException
            || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary> R-29: the export a user asked for from the Supervision of Aetheus (<c>GET
    /// {configuration}/export-request</c>). Aetheus answers 200 with the request identifier once,
    /// to the first poll after the request, and 204 otherwise. Null when nothing is pending or
    /// anything else happens (an older Aetheus answers 404, a network failure): the package then
    /// keeps its periodic exports. </summary>
    internal async Task<string?> ReadExportRequestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(configurationUri, "configuration/export-request"));
        request.Headers.TryAddWithoutValidation(KeyHeader, ingestKey);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                return null;
            var body = await response.Content.ReadFromJsonAsync<ExportRequestBody>(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body?.RequestId) ? null : body.RequestId;
        }
        // Best effort, like the interval: an export request must never stop the application.
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException
            || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private sealed record ExportRequestBody([property: JsonPropertyName("requestId")] string? RequestId);

    private sealed record ConfigurationBody(
        [property: JsonPropertyName("metricExportIntervalSeconds")] int? MetricExportIntervalSeconds);
}
