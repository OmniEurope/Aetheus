// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary> Going back on a finding's decision (accepted, false positive, mitigated), from the
/// Gate tab of a run or from the finding's own page. Asked for confirmation, then the finding is
/// open again; the decision stays in its history. </summary>
internal sealed class AnalysisFindingDecisionRevert(
    ApiClient api,
    OmniDialogService dialog,
    NotifyHelper toast,
    IStringLocalizer<AppStrings> localizer)
{
    /// <summary>A status someone decided by hand, the only ones a revert applies to.</summary>
    public static bool CanRevert(AnalysisFindingStatus status) =>
        status is AnalysisFindingStatus.Accepted or AnalysisFindingStatus.FalsePositive or AnalysisFindingStatus.Mitigated;

    /// <summary>True once the decision is reverted; false when the user went back or the server refused.</summary>
    public async Task<bool> RevertAsync(int findingId)
    {
        var confirmed = await dialog.Confirm(
            localizer["AnalysisRevertDecisionConfirm"].Value,
            localizer["AnalysisRevertDecision"].Value,
            new OmniConfirmOptions
            {
                OkButtonText = localizer["AnalysisReopenFinding"].Value,
                CancelButtonText = localizer["GoBack"].Value
            });
        if (confirmed != true) return false;
        var status = await api.Analysis.RevokeAnalysisFindingDecisionAsync(findingId);
        if (!status.Success)
        {
            toast.Error("Error", "AnalysisRevertDecisionFailed");
            return false;
        }
        toast.Success("Saved", "AnalysisDecisionReverted");
        return true;
    }
}
