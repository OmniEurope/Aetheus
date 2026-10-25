// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Completes a listed run's grade from the assurance contract its own steps published.
/// </summary>
public static class PipelineRunGradeHydration
{
    /// <summary>Output variable carrying the sealed contract's letter, published by
    /// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-candidate-assurance-contract.mjs</c>.</summary>
    private const string AssuranceGradeVariable = "CANDIDATE_ASSURANCE_GRADE";

    /// <summary>
    /// Fills <see cref="PipelineRunDto.GateGrade"/> for a run that owns no analysis evaluation. A
    /// candidate delegates every analysis to a child pipeline, so it holds no evaluation of its own
    /// and its grade read as null: candidate 1899 displayed Success with nothing beside it while its
    /// sealed contract graded it F. The grade is surfaced here, never recomputed, and an absent or
    /// unparsable letter leaves the run ungraded rather than inventing a grade for display.
    /// </summary>
    /// <remarks>
    /// Applied after materialisation because the list projection is translated to SQL.
    /// </remarks>
    public static PipelineRunDto Apply(PipelineRunDto run)
    {
        if (run.GateGrade is not null) return run;

        var letter = run.Steps
            .Select(step => step.OutputVariables.TryGetValue(AssuranceGradeVariable, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return Enum.TryParse<AnalysisGrade>(letter, ignoreCase: true, out var grade)
            ? run with { GateGrade = grade }
            : run;
    }
}
