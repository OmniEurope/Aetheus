// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// R-34: output variables of the steps the backend runs itself, with no agent task and so no task log. They carry
/// the step's one line of account (what it did, or why it did nothing), and the run page shows it where the log of
/// any other step would be, instead of "logs not retained".
/// </summary>
public static class PipelineStepOutputs
{
    /// <summary>What a <c>type: advance-branch</c> step did to the branch and the tag, in one sentence.</summary>
    public const string BranchAdvanceOutcome = "BRANCH_ADVANCE";
}
