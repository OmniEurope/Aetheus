// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;

namespace Aetheus.Front.Components.Shared;

public sealed class ErrorNotificationHandler(NotifyHelper toast, IStringLocalizer<AppStrings> localizer) : DelegatingHandler
{
    private static readonly HashSet<HttpStatusCode> SilentCodes =
    [
        HttpStatusCode.Unauthorized // Handled by AuthDelegatingHandler
    ];

    /// <summary>
    /// Set on a request whose caller reads a 400 itself and shows its reason (the outcome path of
    /// <c>ApiClientBase</c>). Recette R2-039: a refused launch raised this generic "Error 400" toast and
    /// the caller's own toast, two red toasts for one refusal. Only the 400 is left to the caller;
    /// every other failure of such a request is still notified here.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> CallerHandlesBadRequest = new("Aetheus.CallerHandlesBadRequest");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        var loginRequest = request.RequestUri?.AbsolutePath.EndsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase) == true;
        var handledByCaller = response.StatusCode == HttpStatusCode.BadRequest
            && request.Options.TryGetValue(CallerHandlesBadRequest, out var handled) && handled;
        // The backend refuses everything but the password change while one is forced (SITE-SEC-001); the
        // layout already holds the user on that screen, so such a refusal is expected, not an error to show.
        var passwordChangePending = response.Headers.Contains("X-Aetheus-Password-Change-Required");
        if (!response.IsSuccessStatusCode && !SilentCodes.Contains(response.StatusCode) && !loginRequest && !handledByCaller
            && !passwordChangePending)
        {
            // On Blazor WASM the response body is a one-shot BrowserHttpReadStream. Reading it here to
            // surface the API error consumes and disposes it, so HttpClient's own post-pipeline buffering
            // (HttpCompletionOption.ResponseContentRead) then throws ObjectDisposedException and the
            // failure bubbles up to the ErrorBoundary instead of the caller's normal non-2xx handling.
            // Buffering first materialises a re-readable copy so both this read and HttpClient's succeed.
            // Mandatory try/catch: a throwing DelegatingHandler would break every error response.
            try
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ErrorNotification] response buffering failed: {ex.Message}");
            }

            var message = await ExtractErrorMessageAsync(response, cancellationToken);
            var correlationId = response.Headers.TryGetValues("X-Aetheus-Correlation-Id", out var values)
                ? values.FirstOrDefault()
                : null;
            toast.ErrorRaw(
                $"{localizer["Error"]} {(int)response.StatusCode}",
                message,
                correlationId,
                reportClientError: string.IsNullOrWhiteSpace(correlationId));
        }

        return response;
    }

    private async Task<string> ExtractErrorMessageAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType == "application/json"
            || mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var payload = await response.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    if (payload[0] == '"')
                        return System.Text.Json.JsonSerializer.Deserialize<string>(payload, JsonOptions.Web)
                            ?? DefaultMessage(response.StatusCode);
                    var error = System.Text.Json.JsonSerializer.Deserialize<ApiError>(payload, JsonOptions.Web);
                    if (!string.IsNullOrEmpty(error?.Message)) return error.Message;
                    using var document = System.Text.Json.JsonDocument.Parse(payload);
                    if (document.RootElement.TryGetProperty("detail", out var detail)
                        && !string.IsNullOrWhiteSpace(detail.GetString()))
                        return detail.GetString()!;
                    if (document.RootElement.TryGetProperty("title", out var title)
                        && !string.IsNullOrWhiteSpace(title.GetString()))
                        return title.GetString()!;
                }
            }
            catch (Exception ex)
            {
                // Fall through to default message
                System.Diagnostics.Debug.WriteLine($"[ErrorNotification] API error parse failed: {ex.Message}");
            }
        }

        return DefaultMessage(response.StatusCode);
    }

    private string DefaultMessage(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => localizer["InvalidRequest"],
        HttpStatusCode.Forbidden => localizer["AccessDenied"],
        HttpStatusCode.NotFound => localizer["ResourceNotFound"],
        HttpStatusCode.Conflict => localizer["ConflictingOperation"],
        HttpStatusCode.InternalServerError => localizer["UnexpectedServerError"],
        _ => string.Format(localizer["RequestFailed"], (int)statusCode)
    };
}
