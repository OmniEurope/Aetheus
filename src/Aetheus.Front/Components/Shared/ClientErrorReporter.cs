// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;

namespace Aetheus.Front.Components.Shared;

public sealed class ClientErrorReporter(HttpClient http, NavigationManager navigation)
{
    /// <summary>Posts a report to the system log; <paramref name="level"/> is one of
    /// <see cref="ClientErrorLogLevels"/>, null meaning an error.</summary>
    public void Report(string correlationId, string summary, string message, string? stackTrace = null, string? level = null)
    {
        _ = ReportAsync(correlationId, summary, message, stackTrace, level);
    }

    private async Task ReportAsync(string correlationId, string summary, string message, string? stackTrace, string? level)
    {
        try
        {
            await http.PostAsJsonAsync("api/client-errors", new ClientErrorLogRequest
            {
                CorrelationId = correlationId,
                Summary = Truncate(summary, 200),
                Message = Truncate(message, 2000),
                Path = Truncate(PageAddress(), ClientErrorLogRequest.MaxPathLength),
                StackTrace = string.IsNullOrWhiteSpace(stackTrace)
                    ? null
                    : Truncate(stackTrace, ClientErrorLogRequest.MaxStackTraceLength),
                Level = level
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientErrorReporter] report failed: {ex.Message}");
        }
    }

    /// <summary>Report the failing page as an absolute path so the dashboard remains identifiable
    /// as /; omit query and fragment because they may contain tokens or user input.</summary>
    private string PageAddress() =>
        ClientErrorLogRequest.PathWithoutQuery("/" + navigation.ToBaseRelativePath(navigation.Uri));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
