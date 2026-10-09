// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunApprovalPanel : IDisposable
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApplicationVersionState VersionState { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <summary>The run page's approval state, or <c>null</c> before it has loaded.</summary>
    [Parameter] public PipelineRunApprovalController? Approval { get; set; }

    private int? _versionCheckedFor;

    /// <summary>Recette R2-116: the two requests a delivery makes no longer share one title. The
    /// environment's approval authorises the deployment, the stage's own confirmation (R-370) asks
    /// whether the new version works.</summary>
    private string BannerTitle => Approval?.Pending is { Scope: ApprovalScope.Pipeline }
        ? L["PipelineApprovalTitle"]
        : string.Format(L["EnvironmentApprovalTitle"], Approval?.Pending?.EnvironmentName);

    protected override void OnInitialized() => VersionState.Changed += VersionChanged;

    /// <summary>
    /// Recette R2-021: before an approval is offered, the deployed version is read once. The Confirm
    /// approval of a production delivery is requested right after the switch to the new version, and a
    /// page still running the old front must not answer it. No stage marks that approval (an old
    /// backend reads the pipeline YAML strictly, so no new key can be added to it yet), so the rule is
    /// general: while a newer version is deployed, every approval waits for the page to reload. Any
    /// other approval loses nothing to it, the reload is one click and the decision is offered again.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (Approval?.Pending is not { } pending || _versionCheckedFor == pending.Id) return;
        _versionCheckedFor = pending.Id;
        await VersionState.CheckNowAsync();
    }

    private void VersionChanged() => _ = InvokeAsync(StateHasChanged);

    private void Reload() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);

    private bool _commentRequested;

    /// <summary>Recette R2-046: the field shows once asked for, and stays while it holds a comment.</summary>
    private bool CommentOpen => _commentRequested || !string.IsNullOrEmpty(Approval?.Comments);

    private void OpenComment() => _commentRequested = true;

    /// <summary>Recette R2-095: <c>approval-decision</c> marks Approve and Reject, the two buttons that
    /// show their icon alone below phone width (app.css); Comment does not carry it.</summary>
    private string DecisionClass => Approval?.Deciding == true ? "approval-decision btn-busy" : "approval-decision";

    public void Dispose() => VersionState.Changed -= VersionChanged;
}
