// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

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

    public async Task CancelAsync(PipelineRunDto run)
    {
        await veil.RunAsync(async () =>
        {
            var status = await api.Pipelines.CancelPipelineRunAsync(run.Id);
            if (status.Success)
                toast.Info("RunCancelled", "RunCancelledDetail");
            else if (status.Forbidden)
                toast.Error("PipelineRunFailed", "PipelineRunForbidden");
        });
    }

    public Task ShowVariablesAsync(PipelineRunDto run) =>
        dialog.OpenAsync<PipelineRunVariablesDialog>(
            string.Format(localizer["VarsCount"], run.ResolvedVariables.Count),
            new Dictionary<string, object?> { { "Variables", run.ResolvedVariables } },
            new OmniDialogOptions { Width = "600px", AutoFocusFirstElement = false });

    public Task ShowParametersAsync(PipelineRunDto run) =>
        dialog.OpenAsync<PipelineRunParametersDialog>(
            localizer["RunParameters"].Value,
            new Dictionary<string, object?> { { "Parameters", run.Parameters } },
            new OmniDialogOptions { Width = "600px", AutoFocusFirstElement = false });

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
