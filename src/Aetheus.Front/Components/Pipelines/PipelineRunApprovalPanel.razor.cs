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

    /// <summary>Environment approval authorizes deployment; stage confirmation asks whether the
    /// deployed version works.</summary>
    private string BannerTitle => Approval?.Pending is { Scope: ApprovalScope.Pipeline }
        ? L["PipelineApprovalTitle"]
        : string.Format(L["EnvironmentApprovalTitle"], Approval?.Pending?.EnvironmentName);

    protected override void OnInitialized() => VersionState.Changed += VersionChanged;

    /// <summary>Approvals wait for a reload when a newer frontend is deployed, so confirmation uses
    /// the deployed version.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (Approval?.Pending is not { } pending || _versionCheckedFor == pending.Id) return;
        _versionCheckedFor = pending.Id;
        await VersionState.CheckNowAsync();
    }

    private void VersionChanged() => _ = InvokeAsync(StateHasChanged);

    private void Reload() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);

    private bool _commentRequested;

    /// <summary>The field shows once asked for, and stays while it holds a comment.</summary>
    private bool CommentOpen => _commentRequested || !string.IsNullOrEmpty(Approval?.Comments);

    private void OpenComment() => _commentRequested = true;

    /// <summary><c>approval-decision</c> marks Approve and Reject, the two buttons that show their
    /// icon alone below phone width (app.css); Comment does not carry it.</summary>
    private string DecisionClass => Approval?.Deciding == true ? "approval-decision btn-busy" : "approval-decision";

    public void Dispose() => VersionState.Changed -= VersionChanged;
}
