// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// F2: owns the run page's approval-banner state (the run's single Pending approval, the target
/// environment's instructions, and the decide-form fields) and the two calls that drive it. Extracted
/// out of <c>PipelineRun.razor.cs</c> (over the file-size budget) the way <c>PipelineRunStageBaselines</c>
/// and the other page collaborators already are.
/// </summary>
// Public rather than internal only because PipelineRunApprovalPanel takes it as a [Parameter], and a
// Razor component's generated class is public: an internal parameter type will not compile.
public sealed class PipelineRunApprovalController(
    ApiClient api, NotifyHelper toast, Func<Task> reloadRunAsync, PendingApprovalsService? pendingApprovals = null)
{
    public PipelineApprovalDto? Pending { get; private set; }
    public string? EnvironmentInstructions { get; private set; }
    public string? Comments { get; set; }
    public bool Deciding { get; private set; }

    /// <summary>
    /// The run says it waits for an approval, and there is none to make. Either the load failed or the
    /// backend really holds no pending approval for it, and both looked the same from the page: the
    /// panel simply did not appear, on a run that had been waiting for eight days. The page now says
    /// which, instead of showing nothing.
    /// </summary>
    public bool WaitingWithoutApproval { get; private set; }

    /// <summary>True when the pending approval could not be loaded, as opposed to not existing.</summary>
    public bool LoadFailed { get; private set; }

    /// <summary>Loads this run's single Pending approval (if any) and, once per approval, its environment's
    /// instructions. Called on initial load and on every live reload, since SignalR's
    /// ApprovalRequired/ApprovalResolved events only trigger a run reload, not a page navigation.</summary>
    public async Task LoadAsync(PipelineRunDto? run, CancellationToken ct = default)
    {
        if (run is null || run.Status != PipelineStatus.WaitingForApproval)
        {
            Pending = null;
            EnvironmentInstructions = null;
            WaitingWithoutApproval = false;
            LoadFailed = false;
            return;
        }
        try
        {
            var approvals = await api.Pipelines.GetApprovalsAsync(run.Id, ct);
            Pending = approvals.FirstOrDefault(a => a.Status == ApprovalStatus.Pending);
            LoadFailed = false;
            // Instructions belong to the approval being shown and accompany its response, so
            // environment read access and an additional request are unnecessary.
            EnvironmentInstructions = Pending is { Scope: ApprovalScope.Environment }
                ? Pending.EnvironmentInstructions
                : null;
        }
        catch (HttpRequestException)
        {
            Pending = null;
            LoadFailed = true;
        }
        WaitingWithoutApproval = Pending is null;
    }

    /// <summary>The run page reads a waiting run's approval before its first render, so the banner
    /// is there from the start instead of arriving later and pushing the page down. Returns whether
    /// it loaded (the page then skips the load it makes behind the first render).</summary>
    public async Task<bool> LoadIfWaitingAsync(PipelineRunDto? run, CancellationToken ct = default)
    {
        if (run?.Status != PipelineStatus.WaitingForApproval) return false;
        await LoadAsync(run, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>A refused decision preserves the operator's comment and exposes the backend reason.
    /// Both approval banners reload after every response to reflect the current decision
    /// state.</summary>
    public async Task DecideAsync(ApprovalStatus decision)
    {
        if (Pending is null || Deciding) return;
        Deciding = true;
        try
        {
            var request = new ApprovalDecisionRequest { Decision = decision, Comments = Comments };
            var outcome = await api.Pipelines.DecideApprovalAsync(Pending.Id, request);
            if (outcome.IsSuccess)
                Comments = null;
            else if (outcome.Error?.Message is { Length: > 0 } reason)
                toast.Notify(OmniSeverity.Danger, "ApprovalDecisionFailed", reason);
            else
                toast.Error("ApprovalDecisionFailed", "ApprovalDecisionFailedStatus", (int?)outcome.StatusCode ?? 0);
            await reloadRunAsync();
            if (pendingApprovals is not null) await pendingApprovals.ReloadAsync();
        }
        finally
        {
            Deciding = false;
        }
    }

    public void Reset()
    {
        Pending = null;
        EnvironmentInstructions = null;
        Comments = null;
        Deciding = false;
    }
}
