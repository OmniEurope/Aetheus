// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R-22: a stage whose confirmation was missed or refused fails and leaves a warning on the run.
/// Retrying the failed steps replays that stage with a fresh confirmation, so the warning no longer
/// describes the run: it is dropped when the stage is reset, and written again by the refusal path if the
/// stage is refused again.
/// </summary>
internal static class PipelineRunRetryWarnings
{
    /// <summary>The warnings JSON without the stage-confirmation problems of <paramref name="resetStages"/>;
    /// the input unchanged when none applies, <c>null</c> when nothing is left.</summary>
    public static string? WithoutConfirmationOf(string? warningsJson, IReadOnlyCollection<string> resetStages)
    {
        ArgumentNullException.ThrowIfNull(resetStages);
        if (string.IsNullOrEmpty(warningsJson) || resetStages.Count == 0) return warningsJson;
        var warnings = JsonSerializer.Deserialize<List<string>>(warningsJson);
        if (warnings is null) return warningsJson;

        var kept = warnings.Where(warning => !IsConfirmationOf(warning, resetStages)).ToList();
        if (kept.Count == warnings.Count) return warningsJson;
        return kept.Count == 0 ? null : JsonSerializer.Serialize(kept);
    }

    private static bool IsConfirmationOf(string warning, IReadOnlyCollection<string> stages) =>
        StoredProblem.CodeOf(warning) is PipelineProblemCodes.StageConfirmationTimedOut or PipelineProblemCodes.StageConfirmationRejected
        && StoredProblem.ParameterOf(warning, "stage") is { } stage
        && stages.Contains(stage, StringComparer.OrdinalIgnoreCase);
}
