// SPDX-License-Identifier: EUPL-1.2
using System.Net;
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Centralizes launch preflight: invalid YAML aborts launch, and stages without online
/// agents require confirmation.</summary>
public sealed class PipelineRunGate(
    ApiClient api,
    OmniDialogService dialog,
    NotifyHelper toast,
    IStringLocalizer<AppStrings> localizer)
{
    /// <summary>Returns <c>true</c> if the run may proceed, <c>false</c> to abort.</summary>
    /// <param name="parameters">The values chosen for the launch, when they are known: the preview then
    /// resolves the targets a parameter names (the production server) as the launch will.</param>
    public async Task<bool> ConfirmPreflightAsync(
        int pipelineId, string? sourceBranch = null, Dictionary<string, string>? parameters = null) =>
        await ConfirmLaunchAsync(pipelineId, sourceBranch, parameters, offerGateOverride: false)
            != PipelineLaunchConsent.Refused;

    /// <summary>
    /// The preflight of a launch that may deploy a release despite the quality gate (R-72, "Redéployer cette
    /// version"). When the gate holds something against the release, one confirmation lists every reason
    /// first; confirming it is the consent the launch then sends as its override. Nothing against it: no
    /// extra dialog, the launch is an ordinary one.
    /// </summary>
    public async Task<PipelineLaunchConsent> ConfirmLaunchAsync(
        int pipelineId, string? sourceBranch, Dictionary<string, string>? parameters, bool offerGateOverride)
    {
        var pre = await api.Pipelines.PreflightPipelineAsync(pipelineId, sourceBranch, parameters);
        if (NotifyRefusal(pre)) return PipelineLaunchConsent.Refused;

        var despiteGate = false;
        if (offerGateOverride && pre.Value?.GateWarnings is { Count: > 0 } gateWarnings)
        {
            if (!await ConfirmDespiteGateAsync(gateWarnings)) return PipelineLaunchConsent.Refused;
            despiteGate = true;
        }

        if (!await ConfirmStagesAsync(pipelineId, pre.Value)) return PipelineLaunchConsent.Refused;
        return despiteGate ? PipelineLaunchConsent.ProceedDespiteGate : PipelineLaunchConsent.Proceed;
    }

    private async Task<bool> ConfirmDespiteGateAsync(IReadOnlyList<DeploymentGateWarningDto> warnings)
    {
        var confirmed = await dialog.OpenAsync<DeploymentGateOverrideDialog>(
            localizer["GateOverrideTitle"].Value,
            new Dictionary<string, object?> { [nameof(DeploymentGateOverrideDialog.Warnings)] = warnings },
            new OmniDialogOptions
            {
                Width = "min(36rem, 92vw)",
                // docs/contracts/ui-patterns.md: the title must be announced before the first control.
                AutoFocusFirstElement = false
            });
        return confirmed is true;
    }

    private async Task<bool> ConfirmStagesAsync(int pipelineId, PipelinePreflightDto? preflight)
    {
        var warnings = preflight?.Warnings ?? [];
        if (warnings.Count > 0)
        {
            toast.Notify(
                OmniSeverity.Warning,
                "Warnings",
                string.Join("\n", warnings.Select(warning => $"• {LaunchProblemText.DescribeStored(warning, localizer)}")));
        }

        var stages = preflight?.Stages ?? [];
        var unresolved = stages.Where(s => !s.Resolved).ToList();
        if (unresolved.Count == 0) return await ConfirmReadinessAsync(pipelineId);

        // A plain Confirm repeated one full sentence per unresolved stage, so an eleven-stage pipeline
        // rendered eleven copies of the same cause and the dialog became unreadable. The dedicated
        // dialog groups the stages by cause and offers the action that actually fixes it.
        // R-63: when every blocked stage lacks a server choice, the title says so rather than "no agent online".
        var title = unresolved.All(stage => stage.EmptyTargetParameter is not null) ? "PreflightNoServerTitle" : "PreflightNoAgentTitle";
        var confirmed = await dialog.OpenAsync<PipelineRunPreflightDialog>(
            localizer[title].Value,
            new Dictionary<string, object?> { [nameof(PipelineRunPreflightDialog.Unresolved)] = unresolved },
            new OmniDialogOptions
            {
                Width = "min(42rem, 92vw)",
                // docs/contracts/ui-patterns.md: the title must be announced before the first control.
                AutoFocusFirstElement = false
            });
        return confirmed is true && await ConfirmReadinessAsync(pipelineId);
    }

    /// <summary>Shows why the preflight refused the launch, and says whether it did.</summary>
    private bool NotifyRefusal(ApiOutcome<PipelinePreflightDto, YamlValidationResultDto> pre)
    {
        // Read the refusal from its actual response body because middleware errors need not
        // populate the typed Errors collection.
        if (pre.Error is not null || pre.ErrorMessage is not null)
        {
            toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", pre.LocalizedErrorMessage(localizer) ?? string.Empty);
            return true;
        }

        // A failed preflight request must show its error and abort rather than treating missing
        // data as an empty, valid stage list.
        if (!pre.IsTransportFailure) return false;
        var reasonKey = pre.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "PreflightSessionExpired",
            HttpStatusCode.Forbidden => "PreflightForbidden",
            _ => "PreflightServerError"
        };
        toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", localizer[reasonKey].Value);
        return true;
    }

    /// <summary>Offer one nonblocking confirmation for missing run configuration so users can still
    /// proceed knowingly; leave runner findings to agent preflight and ask nothing when the verdict
    /// cannot be read.</summary>
    private async Task<bool> ConfirmReadinessAsync(int pipelineId)
    {
        PipelineSetupReadinessDto? readiness;
        try
        {
            readiness = await api.Pipelines.GetPipelineReadinessAsync(pipelineId);
        }
        catch (HttpRequestException)
        {
            return true;
        }

        var missing = (readiness?.Checks ?? [])
            .Where(check => check.Severity == PipelineSetupReadinessSeverity.Blocking
                && check.Kind is not (PipelineSetupReadinessKind.NoRunnerConfigured or PipelineSetupReadinessKind.NoDeployRunnerConfigured))
            .Select(check => check.Items.Count > 0
                ? $"{localizer[PipelineReadinessText.TitleKey(check.Kind)].Value} : {string.Join(", ", check.Items)}"
                : localizer[PipelineReadinessText.TitleKey(check.Kind)].Value)
            .ToList();
        if (missing.Count == 0) return true;

        var confirmed = await dialog.Confirm(
            string.Format(localizer["PipelineRunNotReadyConfirm"].Value, string.Join(" ; ", missing)),
            localizer["PipelineRunNotReadyTitle"].Value,
            new OmniConfirmOptions { OkButtonText = localizer["RunAnyway"].Value, CancelButtonText = localizer["GoBack"].Value });
        return confirmed == true;
    }
}

/// <summary>What the user agreed to before a launch.</summary>
public enum PipelineLaunchConsent
{
    /// <summary>The launch stops: refused by the preflight, or the user went back.</summary>
    Refused,

    /// <summary>An ordinary launch.</summary>
    Proceed,

    /// <summary>R-72: the user confirmed deploying despite what the quality gate holds against the release.</summary>
    ProceedDespiteGate
}
