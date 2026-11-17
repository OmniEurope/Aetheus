// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Recette R-22: the run page's header offers "retry failed steps" directly, not in its menu, when the run
/// stopped because a stage confirmation was missed: replaying that stage is then the one thing to do.</summary>
internal static class PipelineRunMissedConfirmation
{
    /// <summary>The code of the warning the back writes when a stage's confirmation delay elapsed
    /// (<c>PipelineProblemCodes.StageConfirmationTimedOut</c>); its <c>stage</c> parameter names the stage.</summary>
    internal const string TimedOutCode = "StageConfirmationTimedOut";

    /// <summary>True when a failed step belongs to a stage whose confirmation delay elapsed.</summary>
    public static bool Applies(PipelineRunDto? run) =>
        run is not null
        && run.Warnings
            .Where(warning => StoredProblem.CodeOf(warning) == TimedOutCode)
            .Select(warning => StoredProblem.ParameterOf(warning, "stage"))
            .OfType<string>()
            .Any(stage => run.Steps.Any(step =>
                step.Status == TaskExecutionStatus.Failed
                && string.Equals(step.StageName, stage, StringComparison.OrdinalIgnoreCase)));
}
