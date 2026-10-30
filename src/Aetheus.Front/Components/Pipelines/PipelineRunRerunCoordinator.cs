// SPDX-License-Identifier: EUPL-1.2


namespace Aetheus.Front.Components.Pipelines;

internal sealed class PipelineRunRerunCoordinator(
    ApiClient api,
    NavigationManager navigation,
    NotifyHelper toast,
    OmniDialogService dialog,
    IStringLocalizer<AppStrings> localizer)
{
    public static bool CanRerun(PipelineRunDto? run) => run is
    { Status: var status } && status.IsTerminal();

    public async Task RerunAsync(PipelineRunDto run, string? requestedMode)
    {
        var mode = requestedMode switch
        {
            "sameCommit" => RerunMode.SnapshotSameCommit,
            "branchHead" => RerunMode.SnapshotBranchHead,
            "resumeCheckpoints" => RerunMode.ResumeCheckpoints,
            _ => RerunMode.Current
        };

        if (mode == RerunMode.Current)
        {
            var parameterResult = await TryRerunWithParametersAsync(run);
            if (parameterResult == ParameterRerunResult.Handled) return;
        }
        else if (mode == RerunMode.ResumeCheckpoints)
        {
            PipelineCheckpointResumePreviewDto? preview;
            try { preview = await api.Pipelines.GetCheckpointResumePreviewAsync(run.Id); }
            catch (HttpRequestException)
            {
                toast.Error("PipelineRunFailed", "LoadFailed");
                return;
            }
            if (preview is null)
            {
                toast.Error("PipelineRunFailed", "LoadFailed");
                return;
            }
            var details = string.Join("\n", preview.Items.Select(checkpoint =>
                checkpoint.ReuseCandidate
                    ? $"• {checkpoint.PipelineName}: run #{checkpoint.RunId} - {localizer["CheckpointWillBeRevalidated"]}"
                    : $"• {checkpoint.PipelineName}: {localizer["CheckpointWillReplay"]}"));
            var confirmed = await dialog.Confirm(
                details,
                localizer["ResumeCheckpoints"].Value,
                new OmniConfirmOptions
                {
                    OkButtonText = localizer["Resume"].Value,
                    CancelButtonText = localizer["GoBack"].Value
                });
            if (confirmed != true) return;
        }

        var rerun = await api.Pipelines.RerunPipelineRunAsync(run.Id, mode);
        if (rerun is not null)
            navigation.NavigateTo($"/pipelines/runs/{rerun.Id}");
        else
            toast.Error("PipelineRunFailed", "Error");
    }

    private async Task<ParameterRerunResult> TryRerunWithParametersAsync(PipelineRunDto run)
    {
        // A declaration that cannot be read stops the rerun, and says why (R-47), once.
        var declared = await RunParametersReading.ReadOrNotifyAsync(api, toast, localizer, run.PipelineId, branch: null);
        if (declared is null) return ParameterRerunResult.Handled;
        if (declared.Count == 0) return ParameterRerunResult.NoParameters;

        var result = await dialog.OpenAsync<RunParametersDialog>(
            localizer["RunParameters"].Value,
            new Dictionary<string, object?>
            {
                { "Parameters", declared },
                { "Prefill", run.Parameters }
            },
            new OmniDialogOptions { Width = "560px", AutoFocusFirstElement = false });

        if (result is not Dictionary<string, string> values) return ParameterRerunResult.Handled;

        var outcome = await api.Pipelines.TriggerPipelineRunAsync(run.PipelineId, values);
        if (outcome.Value is not null)
        {
            navigation.NavigateTo($"/pipelines/runs/{outcome.Value.Id}");
        }
        else
        {
            var reason = outcome.LocalizedErrorMessage(localizer) is { } refusal ? refusal
                : outcome.NotFound ? localizer["RunNotFound"].Value
                : outcome.StatusCode == System.Net.HttpStatusCode.Forbidden ? localizer["PipelineRunForbidden"].Value
                : localizer["PipelineRunFailedNoReason"].Value;
            toast.Notify(OmniSeverity.Danger, "PipelineRunFailed", reason);
        }

        return ParameterRerunResult.Handled;
    }

    private enum ParameterRerunResult
    {
        NoParameters,
        Handled
    }
}
