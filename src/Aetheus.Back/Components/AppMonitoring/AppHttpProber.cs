// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>
/// One black-box HTTP availability probe from the backend, through the SSRF-guarded
/// <see cref="HttpClientName"/> client (public URLs only). Shared by the periodic
/// <c>AppProbeService</c> and the immediate probe a user asks for (R-29).
/// </summary>
public sealed class AppHttpProber(IHttpClientFactory httpClientFactory, TimeProvider timeProvider)
{
    /// <summary>Named HttpClient for backend black-box probing (public URLs only, SSRF guard at connect time).</summary>
    public const string HttpClientName = "app-probe";

    public async Task<AppProbeResultDto> ProbeAsync(
        int appId, string url, int expectedStatus, int timeoutSeconds, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
            timeoutSeconds,
            AppMonitoringDefaults.MinimumProbeTimeoutSeconds,
            AppMonitoringDefaults.MaximumProbeTimeoutSeconds)));

        var timestamp = timeProvider.GetUtcNow().UtcDateTime;
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();
            var statusCode = (int)response.StatusCode;
            return new AppProbeResultDto
            {
                MonitoredAppId = appId,
                Timestamp = timestamp,
                IsUp = statusCode == expectedStatus,
                ResponseTimeMs = (int)sw.ElapsedMilliseconds,
                StatusCode = statusCode,
                Error = statusCode == expectedStatus ? null : AppProbeFailure.ForStatus(url, statusCode, expectedStatus)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            sw.Stop();
            var reason = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested
                ? AppProbeFailure.ForTimeout(url)
                : AppProbeFailure.ForException(url, ex);
            return AppProbeResultDto.Failure(
                appId, timestamp, (int)sw.ElapsedMilliseconds, reason);
        }
    }
}
