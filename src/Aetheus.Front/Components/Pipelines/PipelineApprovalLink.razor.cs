// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-24: the approval page an approval request mail links to. Opened with <c>?action=approve</c> or
/// <c>?action=reject</c> by a signed-in reader, it decides the approval through the authenticated API, exactly
/// once: the action is taken off the address before the request (a reload or Back never repeats it), an approval
/// that no longer waits shows its state and is never decided again, and the server still checks the reader's
/// right to decide. <c>?action=comment</c> only opens the comment field of the decision banner: nothing is sent
/// until the reader presses Approve or Reject. A reader who was not signed in reaches the page through the
/// sign-in, which drops the action (<see cref="LoginRedirect"/>): the page then acts on nothing.
/// </summary>
public partial class PipelineApprovalLink
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PendingApprovalsService PendingApprovals { get; set; } = default!;

    [Parameter] public int RunId { get; set; }
    [Parameter] public int ApprovalId { get; set; }

    [SupplyParameterFromQuery(Name = LoginRedirect.OneShotActionParameter)] public string? Action { get; set; }

    private PipelineRunApprovalController? _controller;
    private (int RunId, int ApprovalId)? _loadedFor;
    private bool _loading = true;
    private PipelineApprovalDto? _approval;
    private bool _sawPending;
    private bool _commentAsked;

    private string RunHref => $"/pipelines/runs/{RunId}";

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == (RunId, ApprovalId)) return;
        _loadedFor = (RunId, ApprovalId);
        var decision = Decision(Action);
        _commentAsked = Action == "comment";
        // The action is spent the moment it is read: the address keeps no trace of it.
        if (Action is not null)
            Nav.NavigateTo($"/pipelines/runs/{RunId}/approvals/{ApprovalId}", replace: true);

        _controller = new PipelineRunApprovalController(Api, Toast, ReloadAsync, PendingApprovals);
        _loading = true;
        _sawPending = false;
        await ReloadAsync();
        _sawPending = _approval?.Status == ApprovalStatus.Pending;
        _loading = false;
        Breadcrumb.Set(new BreadcrumbItem(L["Pipelines"], "/pipelines"),
            new BreadcrumbItem(string.Format(L["ApprovalLinkRunCrumb"], RunId), RunHref),
            new BreadcrumbItem(L["ApprovalLinkTitle"]));

        // Only a reader signed in when the link was opened, on this very approval still waiting.
        if (decision is { } wanted && Auth.IsAuthenticated && _controller.Pending?.Id == ApprovalId)
            await _controller.DecideAsync(wanted);
    }

    /// <summary>Reads the approval again (on opening, and after every decision the banner sends).</summary>
    private async Task ReloadAsync()
    {
        try
        {
            var approvals = await Api.Pipelines.GetApprovalsAsync(RunId);
            _approval = approvals.FirstOrDefault(approval => approval.Id == ApprovalId);
        }
        catch (HttpRequestException)
        {
            // A run the reader may not read, or a failed read: the page says the approval is unavailable.
            _approval = null;
        }
        _controller?.Show(_approval);
        // A decision pressed in the banner re-renders the banner only: the page shows the new state too.
        StateHasChanged();
    }

    /// <summary>The decision an action names; none for <c>comment</c> or anything else.</summary>
    internal static ApprovalStatus? Decision(string? action) => action switch
    {
        "approve" => ApprovalStatus.Approved,
        "reject" => ApprovalStatus.Rejected,
        _ => null
    };

    /// <summary>The approval waited when the page opened and this reader has just decided it.</summary>
    private bool DecidedHere => _sawPending && _approval is { Status: ApprovalStatus.Approved or ApprovalStatus.Rejected } decided
        && string.Equals(decided.ResolvedByUsername, Auth.Username, StringComparison.Ordinal);

    private string StageLine => _approval is null ? string.Empty
        : _approval.Scope == ApprovalScope.Pipeline || string.IsNullOrWhiteSpace(_approval.EnvironmentName)
            ? string.Format(L["ApprovalLinkStage"], _approval.StageName)
            : string.Format(L["ApprovalLinkStageEnvironment"], _approval.StageName, _approval.EnvironmentName);

    private string StatusKey => _approval?.Status switch
    {
        ApprovalStatus.Approved => "ApprovalLinkStatusApproved",
        ApprovalStatus.Rejected => "ApprovalLinkStatusRejected",
        ApprovalStatus.TimedOut => "ApprovalLinkStatusTimedOut",
        _ => "ApprovalLinkStatusPending"
    };

    private OmniTone StatusTone => _approval?.Status switch
    {
        ApprovalStatus.Approved => OmniTone.Success,
        ApprovalStatus.Rejected => OmniTone.Danger,
        ApprovalStatus.TimedOut => OmniTone.Warning,
        _ => OmniTone.Info
    };

    private string DecidedLine => _approval is null ? string.Empty
        : _approval.ResolvedAt is { } at && !string.IsNullOrWhiteSpace(_approval.ResolvedByUsername)
            ? string.Format(L["ApprovalLinkDecidedBy"], L[StatusKey], _approval.ResolvedByUsername, at.ToLocalTime().ToString("g"))
            : L[StatusKey];
}
