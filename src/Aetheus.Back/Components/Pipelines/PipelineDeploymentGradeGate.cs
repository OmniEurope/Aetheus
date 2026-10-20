// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The control plane checks the declared deployment-grade threshold before any dispatch;
/// the agent independently verifies the sealed contract. Unreadable thresholds or missing
/// release-grade evidence refuse the attempt.</summary>
internal sealed class PipelineDeploymentGradeGate(IPipelineRepository repo)
{
    internal const string MinimumGradeVariable = "AETHEUS_DEPLOY_MINIMUM_GRADE";
    internal const string ReleaseVariable = "AETHEUS_CANDIDATE_VERSION";

    /// <summary>Null when the run may start, otherwise the refusal naming the grade and the threshold.</summary>
    public async Task<string?> EvaluateAsync(
        int runId, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        if (!TryReadGateInputs(variables, out var threshold, out var selector, out var refusal))
            return refusal;
        var minimum = Enum.Parse<AnalysisGrade>(threshold);

        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = run?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        if (projectId is null)
            return $"Deployment refused before it started: release '{selector}' cannot be checked against the "
                + $"deployment threshold {threshold} because the run is not attached to a project.";

        var release = await repo.FindDeploymentGateReleaseAsync(projectId.Value, selector, ct).ConfigureAwait(false);
        if (release is null)
            return $"Deployment refused before it started: release '{selector}' does not exist in this project, "
                + $"so the deployment threshold {threshold} cannot be checked.";
        if (release.AssuranceGrade is not { } grade)
            return $"Deployment refused before it started: release '{release.Version}' has no sealed assurance grade, "
                + $"and the deployment threshold {threshold} requires one.";
        return grade > minimum
            ? $"Deployment refused before it started: release '{release.Version}' has assurance grade {grade}, "
                + $"below the deployment threshold {threshold}."
            : null;
    }

    /// <summary>
    /// The declared threshold (one capital letter, A to F) and the release it applies to. False when
    /// the gate stops here: with no refusal when no threshold is declared (the run may start), with the
    /// refusal when the threshold is not a grade or no release is named.
    /// </summary>
    internal static bool TryReadGateInputs(
        IReadOnlyDictionary<string, string> variables, out string threshold, out string selector, out string? refusal)
    {
        threshold = string.Empty;
        selector = string.Empty;
        refusal = null;
        if (!variables.TryGetValue(MinimumGradeVariable, out var rawThreshold) || string.IsNullOrWhiteSpace(rawThreshold))
            return false;
        threshold = rawThreshold.Trim();
        // Exactly one capital letter: Enum.TryParse would also accept "4" or "e" and read a typo as a grade.
        if (threshold.Length != 1 || threshold[0] is < 'A' or > 'F')
        {
            refusal = $"Deployment refused before it started: {MinimumGradeVariable} '{threshold}' is not an assurance grade (A to F).";
            return false;
        }

        selector = variables.TryGetValue(ReleaseVariable, out var rawRelease) ? rawRelease.Trim() : string.Empty;
        if (selector.Length == 0 || selector.Contains("$(", StringComparison.Ordinal))
        {
            refusal = $"Deployment refused before it started: the deployment threshold {threshold} is declared but "
                + $"{ReleaseVariable} names no release ('{selector}'), so no grade can be checked.";
            return false;
        }
        return true;
    }
}
