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

    /// <summary>R-72: a launch that may deploy a release despite the quality gate, after the user confirms
    /// every reason the gate has ("Redéployer cette version"). Off for any other launch.</summary>
    public bool OfferGateOverride { get; init; }

    /// <summary>
    /// A plain launch: the declared parameters are taken at their defaults and no dialog opens, unless
    /// a required parameter has no default and the run genuinely cannot be built without an answer.
    /// </summary>
    public async Task<PipelineRunLaunchResult?> LaunchAsync(
        int pipelineId, string? sourceBranch, IReadOnlyDictionary<string, string>? prefill = null)
    {
        // R-35: reading the parameters, the preflight and the trigger each read the repository and take seconds;
        // the click is acknowledged before any of them.
        toast.Info("Running", "PipelineLaunching");
        // The parameters come first: the preview resolves the targets they name (the production server)
        // from them, and a required one nobody answered yet is not a warning to show beforehand.
        var (proceed, parameters) = await dialogs.ResolveDefaultParametersAsync(pipelineId, sourceBranch, prefill);
        if (!proceed) return null;
        var consent = await runGate.ConfirmLaunchAsync(pipelineId, sourceBranch, parameters, OfferGateOverride);
        if (consent == PipelineLaunchConsent.Refused) return null;
        return await TriggerAsync(pipelineId, sourceBranch, parameters,
            gateOverride: consent == PipelineLaunchConsent.ProceedDespiteGate);
    }

    /// <summary>
    /// A launch the user already configured in the unified dialog: branch and parameters are taken as
    /// chosen, with no second prompt.
    /// </summary>
    public async Task<PipelineRunLaunchResult?> LaunchAsync(int pipelineId, PipelineLaunchChoice choice)
    {
        toast.Info("Running", "PipelineLaunching");
        if (!await runGate.ConfirmPreflightAsync(pipelineId, choice.SourceBranch, choice.Parameters)) return null;
        return await TriggerAsync(pipelineId, choice.SourceBranch, choice.Parameters, choice.RunToEnd);
    }

    /// <summary>
    /// Triggers the run as given, with no preflight and no prompt (the caller already confirmed it and
    /// fixed its parameters), then opens the new run or reports why it was refused.
    /// </summary>
    public async Task<PipelineRunLaunchResult?> TriggerAsync(
        int pipelineId, string? sourceBranch, Dictionary<string, string>? parameters, bool runToEnd = false,
        bool gateOverride = false)
    {
        var outcome = await api.Pipelines.TriggerPipelineRunAsync(
            pipelineId, parameters, sourceBranch, runToEnd: runToEnd, gateOverride: gateOverride);
        if (outcome.Value is not null)
        {
            // Straight to the new run, in the SPA, unless the caller shows it where the user already is.
            toast.Info("Running", "PipelineTriggered");
            if (OpenLaunchedRun) navigation.NavigateTo($"/pipelines/runs/{outcome.Value.Id}");
            return new PipelineRunLaunchResult(outcome.Value);
        }

        // Error payloads may omit or null the errors array; extract the refusal reason without
        // assuming its shape. A refusal stated as problem codes is said in the user's language.
        var reason = outcome.LocalizedErrorMessage(localizer) is { } refusal ? refusal
            : outcome.NotFound ? localizer["RunNotFound"].Value
            : outcome.StatusCode == System.Net.HttpStatusCode.Forbidden ? localizer["PipelineRunForbidden"].Value
            : localizer["PipelineRunFailedNoReason"].Value;
        toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", reason);
        return null;
    }
}
