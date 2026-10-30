// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Reads the parameters a pipeline launch declares and, when it cannot, tells the user why with exactly one
/// danger notification (R-47): no launch path may stop in silence, and none may stop with two toasts.
/// </summary>
internal static class RunParametersReading
{
    /// <summary>
    /// The declared parameters, or <c>null</c> after the reason was shown: the API's own message when it gave
    /// one (an invalid pipeline YAML answers 400 with its reason), else a sentence by status, else "server
    /// unreachable". The read tells the global notifier it handles every error itself, so a 403 or a 500
    /// does not raise a second toast beside this one.
    /// </summary>
    public static async Task<List<PipelineRunParameterDto>?> ReadOrNotifyAsync(
        ApiClient api,
        NotifyHelper toast,
        IStringLocalizer<AppStrings> localizer,
        int pipelineId,
        string? branch)
    {
        ApiOutcome<List<PipelineRunParameterDto>, ApiError> outcome;
        try
        {
            outcome = await api.Pipelines.GetPipelineRunParametersOutcomeAsync(pipelineId, branch).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            NotifyUnreachable(toast, localizer);
            return null;
        }

        if (outcome.Value is { } declared) return declared;
        var reason = outcome.ErrorMessage is { } message ? message
            : outcome.NotFound ? localizer["RunNotFound"].Value
            : outcome.StatusCode switch
            {
                HttpStatusCode.Unauthorized => localizer["PreflightSessionExpired"].Value,
                HttpStatusCode.Forbidden => localizer["PreflightForbidden"].Value,
                _ => localizer["RunParametersReadFailed"].Value
            };
        toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", reason);
        return null;
    }

    /// <summary>The request itself failed (no answer, timeout, open circuit, a body that is not JSON): nothing
    /// reached the global notifier, so this is the one toast.</summary>
    public static void NotifyUnreachable(NotifyHelper toast, IStringLocalizer<AppStrings> localizer) =>
        toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", localizer["RunParametersServerUnreachable"].Value);

    /// <summary>
    /// Everything a read of this API can throw short of a bug. <see cref="HttpRequestException"/>
    /// alone is not enough: the front's handlers add resilience (timeout rejection, open circuit)
    /// and a proxy answering HTML with a 200 surfaces as a <see cref="JsonException"/>. Letting any
    /// of those escape would make a decorative load take down the launch dialog itself.
    /// </summary>
    public static bool IsTransportFailure(Exception exception) =>
        exception is HttpRequestException
            or JsonException
            or OperationCanceledException
            or TimeoutRejectedException
            or BrokenCircuitException;
}
