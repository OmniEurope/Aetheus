// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Shared pre-flight gate before launching a pipeline run. Resolves each stage's target server
/// up-front and, if any stage has no online agent, asks the user to confirm (or aborts). Invalid
/// YAML is surfaced here too so the user is never sent into a doomed run. Centralises the logic
/// previously duplicated in PipelineEdit and Pipelines.
/// </summary>
public sealed class PipelineRunGate(
    ApiClient api,
    OmniDialogService dialog,
    NotifyHelper toast,
    IStringLocalizer<AppStrings> localizer)
{
    /// <summary>Returns <c>true</c> if the run may proceed, <c>false</c> to abort.</summary>
    public async Task<bool> ConfirmPreflightAsync(int pipelineId, string? sourceBranch = null)
    {
        var pre = await api.Pipelines.PreflightPipelineAsync(pipelineId, sourceBranch);

        // R2-039: a refused preflight is read from its body whatever its shape; Error.Errors is null
        // when the body is the error middleware's object, and joining it threw into the error boundary.
        if (pre.Error is not null || pre.ErrorMessage is not null)
        {
            toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", pre.ErrorMessage ?? string.Empty);
            return false;
        }

        // S-UX-TRGE: a 401/403/500 preflight previously left both Value and Error null and the gate
        // fell through to an empty stage list, returning true and dispatching a doomed run. Surface the
        // real reason and abort instead of silently proceeding.
        if (pre.IsTransportFailure)
        {
            var reasonKey = pre.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "PreflightSessionExpired",
                HttpStatusCode.Forbidden => "PreflightForbidden",
                _ => "PreflightServerError"
            };
            toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", localizer[reasonKey].Value);
            return false;
        }

        var warnings = pre.Value?.Warnings ?? [];
        if (warnings.Count > 0)
        {
            toast.Notify(
                OmniSeverity.Warning,
                "Warnings",
                string.Join("\n", warnings.Select(warning => $"• {warning}")));
        }

        var stages = pre.Value?.Stages ?? [];
        var unresolved = stages.Where(s => !s.Resolved).ToList();
        if (unresolved.Count == 0)
        {
            // Show which runners were picked (fallback transparency)
            var fallbacks = stages.Where(s => s.Resolved && s.ServerName is not null).ToList();
            if (fallbacks.Count > 0)
            {
                var info = string.Join("\n", fallbacks.Select(s =>
                    $"• {s.StageName}: {s.ServerName}" + (s.Reason is not null ? $" ({s.Reason})" : "")));
                toast.Notify(OmniSeverity.Info, "PreflightResolved", info);
            }
            return await ConfirmReadinessAsync(pipelineId);
        }

        // A plain Confirm repeated one full sentence per unresolved stage, so an eleven-stage pipeline
        // rendered eleven copies of the same cause and the dialog became unreadable. The dedicated
        // dialog groups the stages by cause and offers the action that actually fixes it.
        var confirmed = await dialog.OpenAsync<PipelineRunPreflightDialog>(
            localizer["PreflightNoAgentTitle"].Value,
            new Dictionary<string, object?> { [nameof(PipelineRunPreflightDialog.Unresolved)] = unresolved },
            new OmniDialogOptions
            {
                Width = "min(42rem, 92vw)",
                // docs/contracts/ui-patterns.md: the title must be announced before the first control.
                AutoFocusFirstElement = false
            });
        return confirmed is true && await ConfirmReadinessAsync(pipelineId);
    }

    /// <summary>
    /// Recette R1-28: what is not configured and would make this run fail (an adapter script or the
    /// scanner configuration absent from the repository, a missing environment, a library or vault a
    /// template requires), named in one confirmation. It never blocks: the user may run anyway. Runner
    /// findings are left to the agent preflight above, which already asked about them. A verdict that
    /// cannot be read asks nothing.
    /// </summary>
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
