// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Recette R-39: the run page's region for a run waiting for a resume after a missed
/// deployment confirmation. It owns both answers, Resume and Cancel, and tells the page to reload once
/// the server accepted either.</summary>
public partial class PipelineRunResumePanel
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    /// <summary>The run shown by the page; the panel renders only while it waits for a resume.</summary>
    [Parameter] public PipelineRunDto? Run { get; set; }

    /// <summary>Raised after a resume or a cancellation was sent, so the page reloads the run.</summary>
    [Parameter] public EventCallback Changed { get; set; }

    private bool _resuming;
    private bool _cancelling;

    private bool Working => _resuming || _cancelling;

    /// <summary>The deadline in the browser's time zone; the server stores it in UTC.</summary>
    private string DetailText => string.Format(
        L["RunAwaitingResumeDetail"].Value,
        Run?.ResumeDeadline is { } deadline ? deadline.ToLocalTime().ToString("g") : L["Unknown"].Value);

    private async Task ResumeAsync()
    {
        if (Run is null || Working) return;
        _resuming = true;
        try
        {
            var status = await Api.Pipelines.ResumePipelineRunAsync(Run.Id);
            if (status.Success)
                Toast.Info("RunResumed", "RunResumedDetail");
            else if (status.Forbidden)
                Toast.Error("PipelineRunFailed", "PipelineRunForbidden");
            else
                Toast.Error("RunResumeFailed", "RunResumeFailedDetail");
            await Changed.InvokeAsync();
        }
        finally
        {
            _resuming = false;
        }
    }

    private async Task CancelAsync()
    {
        if (Run is null || Working) return;
        var confirmed = await Dialog.Confirm(
            L["CancelAwaitingRunConfirm"].Value,
            L["CancelRun"].Value,
            new OmniConfirmOptions
            {
                Destructive = true,
                ConfirmIcon = OmniIconName.Stop,
                OkButtonText = L["Stop"].Value,
                CancelButtonText = L["GoBack"].Value
            });
        if (confirmed != true) return;

        _cancelling = true;
        try
        {
            var run = Run;
            await Veil.RunAsync(async () =>
            {
                var status = await Api.Pipelines.CancelPipelineRunAsync(run.Id);
                if (status.Success)
                    Toast.Info("RunCancelled", "RunCancelledDetail");
                else if (status.Forbidden)
                    Toast.Error("PipelineRunFailed", "PipelineRunForbidden");
                else
                    Toast.Error("PipelineRunFailed", "Error");
                await Changed.InvokeAsync();
            });
        }
        finally
        {
            _cancelling = false;
        }
    }
}
