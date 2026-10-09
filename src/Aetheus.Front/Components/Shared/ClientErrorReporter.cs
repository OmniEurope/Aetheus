// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;

namespace Aetheus.Front.Components.Shared;

public sealed class ClientErrorReporter(HttpClient http, NavigationManager navigation)
{
    public void Report(string correlationId, string summary, string message, string? stackTrace = null)
    {
        _ = ReportAsync(correlationId, summary, message, stackTrace);
    }

    private async Task ReportAsync(string correlationId, string summary, string message, string? stackTrace)
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
                    : Truncate(stackTrace, ClientErrorLogRequest.MaxStackTraceLength)
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[ClientErrorReporter] report failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The page the error happened on, as an absolute path ("/" for the dashboard, which a base-relative
    /// path rendered as nothing: recette R2-100). The query string and fragment are left out: they can
    /// carry a token or a search the user typed, and the route alone says which screen failed.
    /// </summary>
    private string PageAddress() =>
        ClientErrorLogRequest.PathWithoutQuery("/" + navigation.ToBaseRelativePath(navigation.Uri));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
