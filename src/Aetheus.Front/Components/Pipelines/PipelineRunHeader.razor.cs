// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunHeader
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;
    [Parameter, EditorRequired] public string PipelineHref { get; set; } = string.Empty;
    [Parameter] public string? GitBranch { get; set; }
    /// <summary>Number of reasons the run is dispatching nothing; the detail is in the overview.</summary>
    [Parameter] public int WaitingReasonCount { get; set; }

    /// <summary>True when the analysis gate could not be computed from every child run.</summary>
    [Parameter] public bool GateIncomplete { get; set; }

    [Parameter] public int BlockingWarningCount { get; set; }
    [Parameter] public int InfoWarningCount { get; set; }
    [Parameter] public bool RunInProgress { get; set; }
    [Parameter] public bool CanRerun { get; set; }
    [Parameter] public bool HasFailedSteps { get; set; }
    /// <summary>R-22: the run stopped on a missed stage confirmation, so "retry failed steps" is a button of the
    /// header rather than a menu entry.</summary>
    [Parameter] public bool ConfirmationMissed { get; set; }
    private bool RetryFailedPinned => CanRerun && HasFailedSteps && ConfirmationMissed;
    [Parameter] public bool Cancelling { get; set; }
    [Parameter] public bool Rerunning { get; set; }
    [Parameter] public bool Retrying { get; set; }
    [Parameter] public EventCallback VariablesRequested { get; set; }
    [Parameter] public EventCallback EditRequested { get; set; }
    [Parameter] public EventCallback CancelRequested { get; set; }
    [Parameter] public EventCallback<string?> RerunRequested { get; set; }
    [Parameter] public EventCallback RetryFailedRequested { get; set; }
}
