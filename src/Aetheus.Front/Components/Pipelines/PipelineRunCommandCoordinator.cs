// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the server answered to a run cancellation, as the screen has to act on it.</summary>
internal enum PipelineRunCancelOutcome
{
    /// <summary>The cancellation was recorded.</summary>
    Accepted,

    /// <summary>The run is finished or gone (404): the screen showed a stale row, which has to be read again.</summary>
    NoLongerCancellable,

    /// <summary>Any other refusal (forbidden, server error): said in a toast, the row stays as it is.</summary>
    Refused
}

internal sealed class PipelineRunCommandCoordinator(
    ApiClient api,
    OmniDialogService dialog,
    NotifyHelper toast,
    IStringLocalizer<AppStrings> localizer,
    IJSRuntime js,
    BusyVeil veil)
{
    public async Task<bool> RetryFailedAsync(PipelineRunDto run)
    {
        var retried = await api.Pipelines.RetryFailedStepsAsync(run.Id);
        if (retried is not null)
            return true;

        toast.Error("PipelineRunFailed", "Error");
        return false;
    }

    public async Task<bool> ConfirmCancellationAsync()
    {
        var confirmed = await dialog.Confirm(
            localizer["CancelRunConfirm"].Value,
            localizer["CancelRun"].Value,
            new OmniConfirmOptions
            {
                Destructive = true,
                ConfirmIcon = OmniIconName.Stop,
                OkButtonText = localizer["Stop"].Value,
                CancelButtonText = localizer["GoBack"].Value
            });
        return confirmed == true;
    }

    /// <summary>Cancels the run and says the answer in a toast; the outcome tells the caller whether the row has to be read again.</summary>
    internal async Task<PipelineRunCancelOutcome> CancelAsync(PipelineRunDto run)
    {
        var outcome = PipelineRunCancelOutcome.Refused;
        await veil.RunAsync(async () =>
        {
            var status = await api.Pipelines.CancelPipelineRunAsync(run.Id);
            if (status.Success)
            {
                outcome = PipelineRunCancelOutcome.Accepted;
                toast.Info("RunCancelled", "RunCancelledDetail");
            }
            else if (status.NotFound)
            {
                // A run already finished (404) is an expected race with a stale list, not a failure: a warning that
                // names it, nothing reported as a client error.
                outcome = PipelineRunCancelOutcome.NoLongerCancellable;
                toast.Warning("CancelRun", "RunNoLongerCancellable");
            }
            else if (status.Forbidden)
                toast.Error("PipelineRunFailed", "PipelineRunForbidden");
            else
                toast.Error("PipelineRunFailed", "Error");
        });
        return outcome;
    }

    public Task ShowVariablesAsync(PipelineRunDto run) =>
        dialog.OpenAsync<PipelineRunVariablesDialog>(
            string.Format(localizer["VarsCount"], run.ResolvedVariables.Count),
            new Dictionary<string, object?> { { "Variables", run.ResolvedVariables } },
            new OmniDialogOptions { Width = "600px", AutoFocusFirstElement = false });

    public Task ShowParametersAsync(PipelineRunDto run) =>
        dialog.OpenAsync<PipelineRunParametersDialog>(
            localizer["RunParameters"].Value,
            new Dictionary<string, object?> { { "Parameters", run.Parameters }, { "RunId", run.Id } },
            new OmniDialogOptions { Width = "900px", AutoFocusFirstElement = false });

    public async Task CopyLogsAsync(IReadOnlyCollection<TaskLogDto> logs)
    {
        await js.InvokeVoidAsync(
            "Aetheus.copyToClipboard",
            string.Join("\n", logs.Select(log => log.Message)));
        toast.Success("LogsCopied");
    }

    public async Task DownloadArtifactAsync(PipelineArtifactDto artifact)
    {
        var stream = await api.ServerTools.DownloadArtifactAsync(artifact.Id);
        if (stream is null)
            return;

        using var streamReference = new DotNetStreamReference(stream);
        await js.InvokeVoidAsync(
            "downloadFileFromStream",
            $"{artifact.Name}.zip",
            streamReference);
    }

    public async Task CopyYamlAsync(string yaml)
    {
        await js.InvokeVoidAsync("Aetheus.copyToClipboard", yaml);
        toast.Success("Copied");
    }
}
