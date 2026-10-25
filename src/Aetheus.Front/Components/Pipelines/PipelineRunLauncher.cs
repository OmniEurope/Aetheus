// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

internal sealed class PipelineRunLauncher(
    ApiClient api,
    PipelineRunGate runGate,
    PipelineRunDialogCoordinator dialogs,
    NavigationManager navigation,
    NotifyHelper toast,
    IStringLocalizer<AppStrings> localizer)
{
    /// <summary>Successful launch opens the run by default; callers may retain their runs grid
    /// instead.</summary>
    public bool OpenLaunchedRun { get; init; } = true;

    /// <summary>
    /// A plain launch: the declared parameters are taken at their defaults and no dialog opens, unless
    /// a required parameter has no default and the run genuinely cannot be built without an answer.
    /// </summary>
    public async Task<PipelineRunLaunchResult?> LaunchAsync(int pipelineId, string? sourceBranch)
    {
        // The parameters come first: the preview resolves the targets they name (the production server)
        // from them, and a required one nobody answered yet is not a warning to show beforehand.
        var (proceed, parameters) = await dialogs.ResolveDefaultParametersAsync(pipelineId, sourceBranch);
        if (!proceed) return null;
        if (!await runGate.ConfirmPreflightAsync(pipelineId, sourceBranch, parameters)) return null;
        return await TriggerAsync(pipelineId, sourceBranch, parameters);
    }

    /// <summary>
    /// A launch the user already configured in the unified dialog: branch and parameters are taken as
    /// chosen, with no second prompt.
    /// </summary>
    public async Task<PipelineRunLaunchResult?> LaunchAsync(int pipelineId, PipelineLaunchChoice choice)
    {
        if (!await runGate.ConfirmPreflightAsync(pipelineId, choice.SourceBranch, choice.Parameters)) return null;
        return await TriggerAsync(pipelineId, choice.SourceBranch, choice.Parameters, choice.RunToEnd);
    }

    /// <summary>
    /// Triggers the run as given, with no preflight and no prompt (the caller already confirmed it and
    /// fixed its parameters), then opens the new run or reports why it was refused.
    /// </summary>
    public async Task<PipelineRunLaunchResult?> TriggerAsync(
        int pipelineId, string? sourceBranch, Dictionary<string, string>? parameters, bool runToEnd = false)
    {
        var outcome = await api.Pipelines.TriggerPipelineRunAsync(pipelineId, parameters, sourceBranch, runToEnd: runToEnd);
        if (outcome.Value is not null)
        {
            // Straight to the new run, in the SPA, unless the caller shows it where the user already is.
            toast.Info("Running", "PipelineTriggered");
            if (OpenLaunchedRun) navigation.NavigateTo($"/pipelines/runs/{outcome.Value.Id}");
            return new PipelineRunLaunchResult(outcome.Value);
        }

        // Error payloads may omit or null the errors array; extract the refusal reason without
        // assuming its shape.
        var reason = outcome.ErrorMessage is { } refusal ? refusal
            : outcome.NotFound ? localizer["RunNotFound"].Value
            : outcome.StatusCode == System.Net.HttpStatusCode.Forbidden ? localizer["PipelineRunForbidden"].Value
            : localizer["PipelineRunFailedNoReason"].Value;
        toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", reason);
        return null;
    }
}
