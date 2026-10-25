// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Components.Releases;

public sealed record AssuranceVerdict(AnalysisGrade? Grade, bool? Deployable, int? BlockingTestCount);

/// <summary>
/// F1: reads the CANDIDATE_ASSURANCE_GRADE / CANDIDATE_DEPLOYABLE / CANDIDATE_BLOCKING_TESTS output
/// variables the AssuranceSeal stage published on a run (see
/// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-candidate-assurance-contract.mjs</c> and the inline seal step of
/// <c>.pipeline/aetheus-candidate.yaml</c>), the same output-variable scan
/// <see cref="PipelineRunGradeAggregation"/> already uses for the run list's grade column. Extracted out
/// of <see cref="ReleaseService"/> (over the file-size budget), not for reuse elsewhere yet.
/// </summary>
public static class ReleaseAssuranceVerdictReader
{
    /// <summary>Returns null when the run carries none of the three variables (a legacy release, or one
    /// published outside the candidate pipeline) or when <paramref name="pipelineRepo"/> is null.</summary>
    public static async Task<AssuranceVerdict?> ReadAsync(
        IPipelineRepository? pipelineRepo, int pipelineRunId, CancellationToken ct)
    {
        if (pipelineRepo is null) return null;

        var stepOutputs = await pipelineRepo.GetSuccessfulStepOutputsAsync(pipelineRunId, ct).ConfigureAwait(false) ?? [];
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in stepOutputs)
            foreach (var (key, value) in PipelineRunHelpers.DeserializeResolvedVariables(step.OutputVariablesJson))
                vars.TryAdd(key, value);

        if (!vars.TryGetValue("CANDIDATE_ASSURANCE_GRADE", out var gradeLetter)
            && !vars.ContainsKey("CANDIDATE_DEPLOYABLE"))
            return null;

        AnalysisGrade? grade = Enum.TryParse<AnalysisGrade>(gradeLetter, ignoreCase: true, out var parsedGrade)
            ? parsedGrade
            : null;
        bool? deployable = vars.TryGetValue("CANDIDATE_DEPLOYABLE", out var deployableRaw)
            && bool.TryParse(deployableRaw, out var parsedDeployable)
            ? parsedDeployable
            : null;
        int? blockingCount = vars.TryGetValue("CANDIDATE_BLOCKING_TESTS", out var blockingRaw)
            ? blockingRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length
            : null;

        return new AssuranceVerdict(grade, deployable, blockingCount);
    }
}
