// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

public static class AnalysisFindingStatusExtensions
{
    /// <summary>True for the statuses a reviewer chooses: accepted, false positive or mitigated.
    /// <see cref="AnalysisFindingStatus.Fixed"/> is not a decision, it is set when the finding is gone from
    /// the code (recette R1-35), so a fixed finding is neither counted nor listed as decided. The
    /// database queries spell the same three statuses out, as a method cannot be translated.</summary>
    public static bool IsDecision(this AnalysisFindingStatus status) =>
        status is AnalysisFindingStatus.Accepted or AnalysisFindingStatus.FalsePositive or AnalysisFindingStatus.Mitigated;
}
