// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The release a deployment launch names, and what the quality gate holds against it.</summary>
internal sealed record DeploymentGateAssessment(
    string Selector, DeploymentGateRelease? Release, List<DeploymentGateWarningDto> Warnings);

/// <summary>
/// R-72 (owner decision of 2026-10-28): "a release can always be deployed again; the only case it cannot is
/// when its artifacts no longer exist". This lists everything the quality gate holds against the release a
/// deployment pipeline is launched with, for the confirmation the launch shows (preflight) and for the record
/// a launch made despite them leaves on the run. A deployment pipeline is one that declares the
/// <c>candidateVersion</c> parameter, as for <see cref="PipelineReleaseGateVariables"/>.
/// </summary>
internal static class PipelineDeploymentGateWarnings
{
    /// <summary>The assessment of the release the launch names, or null when the pipeline is not a
    /// deployment, or names no release (no value, or one still to be expanded).</summary>
    public static async Task<DeploymentGateAssessment?> AssessAsync(
        PipelineYamlDefinition definition, IReadOnlyDictionary<string, string> variables, int? projectId,
        IPipelineRepository repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(variables);
        var declared = definition.Parameters.Any(parameter => string.Equals(
            parameter.Name, PipelineReleaseGateVariables.CandidateVersionParameter, StringComparison.OrdinalIgnoreCase));
        if (!declared || projectId is not { } id) return null;

        var parameterKey = PipelineCommandBuilder.ParameterKeyPrefix + PipelineReleaseGateVariables.CandidateVersionParameter;
        var selector = variables.TryGetValue(parameterKey, out var raw) ? raw.Trim() : string.Empty;
        if (selector.Length == 0
            || selector.Contains("$(", StringComparison.Ordinal)
            || selector.Contains("${{", StringComparison.Ordinal))
            return null;

        var release = await repo.FindDeploymentGateReleaseAsync(id, selector, ct).ConfigureAwait(false);
        return new DeploymentGateAssessment(
            selector, release, release is null ? [] : Collect(release, ReadMinimum(variables)));
    }

    /// <summary>Every reason, in the order the confirmation lists them: the grade first, then the verdict,
    /// the tests and the security findings.</summary>
    internal static List<DeploymentGateWarningDto> Collect(DeploymentGateRelease release, AnalysisGrade? minimum)
    {
        ArgumentNullException.ThrowIfNull(release);
        var warnings = new List<DeploymentGateWarningDto>();
        if (release.AssuranceGrade is not { } grade)
            warnings.Add(new DeploymentGateWarningDto { Kind = DeploymentGateWarningKind.NoGrade });
        else if (minimum is { } threshold && grade > threshold)
            warnings.Add(new DeploymentGateWarningDto
            {
                Kind = DeploymentGateWarningKind.BelowMinimum,
                Grade = grade,
                MinimumGrade = threshold
            });
        else if (grade == AnalysisGrade.F)
            warnings.Add(new DeploymentGateWarningDto { Kind = DeploymentGateWarningKind.GradedF, Grade = grade });

        if (release.Deployable == false)
            warnings.Add(new DeploymentGateWarningDto { Kind = DeploymentGateWarningKind.Rejected });
        // Without a grade there is no verdict either: the "no grade" line already says it.
        else if (release.Deployable is null && release.AssuranceGrade is not null)
            warnings.Add(new DeploymentGateWarningDto { Kind = DeploymentGateWarningKind.NoVerdict });

        if (release.BlockingTestCount is > 0 and var tests)
            warnings.Add(new DeploymentGateWarningDto { Kind = DeploymentGateWarningKind.BlockingTests, Count = tests });
        if (release.OpenSecurityFindingCount > 0)
            warnings.Add(new DeploymentGateWarningDto
            {
                Kind = DeploymentGateWarningKind.SecurityFindings,
                Count = release.OpenSecurityFindingCount
            });
        return warnings;
    }

    /// <summary>The declared deployment threshold, or null when none is declared or it is not a grade
    /// (the grade gate refuses that launch on its own, override or not).</summary>
    private static AnalysisGrade? ReadMinimum(IReadOnlyDictionary<string, string> variables) =>
        variables.TryGetValue(PipelineDeploymentGradeGate.MinimumGradeVariable, out var raw)
        && raw.Trim() is { Length: 1 } letter && letter[0] is >= 'A' and <= 'F'
            ? Enum.Parse<AnalysisGrade>(letter)
            : null;
}
