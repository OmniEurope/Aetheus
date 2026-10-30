// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunApprovalPanel : IDisposable
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApplicationVersionState VersionState { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

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

    /// <summary>Recette R-24: the field is open from the start (an approval mail's Comment link); nothing is
    /// sent until Approve or Reject is pressed.</summary>
    [Parameter] public bool StartWithComment { get; set; }

    private bool _commentRequested;

    /// <summary>The field shows once asked for, and stays while it holds a comment.</summary>
    private bool CommentOpen => StartWithComment || _commentRequested || !string.IsNullOrEmpty(Approval?.Comments);

    private void OpenComment() => _commentRequested = true;

    internal const string CommentFieldId = "approval-comment";

    private bool _commentFocused;

    /// <summary>R-24: a reader who came by the mail's Comment link types at once; the field gets the focus
    /// the first time it is drawn (moving the focus from code is allowed, only a dialog title is not).</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_commentFocused || !StartWithComment || Approval?.Pending is null || VersionState.NewVersionAvailable)
            return;
        _commentFocused = true;
        await Js.InvokeVoidAsync("Aetheus.focusById", CommentFieldId);
    }

    /// <summary><c>approval-decision</c> marks Approve and Reject, the two buttons that show their
    /// icon alone below phone width (app.css); Comment does not carry it.</summary>
    private string DecisionClass => Approval?.Deciding == true ? "approval-decision btn-busy" : "approval-decision";

    public void Dispose() => VersionState.Changed -= VersionChanged;
}
