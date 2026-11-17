// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-24: the approval page an approval request mail links to. Opened with <c>?action=approve</c> or
/// <c>?action=reject</c> by a signed-in reader, it decides the approval through the authenticated API, exactly
/// once: the action is taken off the address before the request (a reload or Back never repeats it), an approval
/// that no longer waits shows its state and is never decided again, and the server still checks the reader's
/// right to decide. Once the action is taken the reader lands on the run page, whose banner shows the approval
/// state (R-13); <c>?action=comment</c> has nothing to take and lands there at once. A failed or refused action
/// keeps the reader here, with the error visible. A reader who was not signed in reaches the page through the
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

    private string RunHref => $"/pipelines/runs/{RunId}";

    // The number is known once the approval is read; a run the reader may not read stays unnumbered.
    private string RunCrumb => PipelineRunPresentation.Label(L["PipelineRun"], _approval?.PipelineRunNumber);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == (RunId, ApprovalId)) return;
        _loadedFor = (RunId, ApprovalId);
        var decision = Decision(Action);
        var commentOnly = Action == "comment";
        // The action is spent the moment it is read: the address keeps no trace of it.
        if (Action is not null)
            Nav.NavigateTo($"/pipelines/runs/{RunId}/approvals/{ApprovalId}", replace: true);
        // R-13: Comment has no one-click action; the run page's banner holds the comment field.
        if (commentOnly)
        {
            Nav.NavigateTo(RunHref, replace: true);
            return;
        }

        var controller = new PipelineRunApprovalController(Api, Toast, ReloadAsync, PendingApprovals);
        _controller = controller;
        var target = (RunId, ApprovalId);
        _loading = true;
        _sawPending = false;
        await ReloadAsync();
        // F-FRONT-007: the reader may have moved to another approval while the read was in flight; that
        // approval's own pass owns the page now, and this link's decision must never land on it.
        if (!ReferenceEquals(_controller, controller) || (RunId, ApprovalId) != target) return;
        _sawPending = _approval?.Status == ApprovalStatus.Pending;
        _loading = false;
        Breadcrumb.Set(new BreadcrumbItem(L["Pipelines"], "/pipelines"),
            new BreadcrumbItem(RunCrumb, RunHref),
            new BreadcrumbItem(L["ApprovalLinkTitle"]));

        // Only a reader signed in when the link was opened, on this very approval still waiting.
        if (decision is { } wanted && Auth.IsAuthenticated && controller.Pending?.Id == target.ApprovalId)
        {
            await controller.DecideAsync(wanted);
            // R-13: the action succeeded (the approval was read back as decided by this reader): the run page
            // shows the result. Anything else (refused, failed) stays here so the reader sees why.
            if (ReferenceEquals(_controller, controller) && DecidedHere)
                Nav.NavigateTo(RunHref, replace: true);
        }
    }

    /// <summary>Reads the approval again (on opening, and after every decision the banner sends).</summary>
    private async Task ReloadAsync()
    {
        var target = (RunId, ApprovalId);
        PipelineApprovalDto? read;
        try
        {
            var approvals = await Api.Pipelines.GetApprovalsAsync(target.RunId);
            read = approvals.FirstOrDefault(approval => approval.Id == target.ApprovalId);
        }
        catch (HttpRequestException)
        {
            // A run the reader may not read, or a failed read: the page says the approval is unavailable.
            read = null;
        }
        // F-FRONT-007: a read answered after the reader moved to another approval belongs to the old one.
        if ((RunId, ApprovalId) != target) return;
        _approval = read;
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
