// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R-24: what the Gate said about the release a deployment names, handed to the run as variables so a
/// deployment template can ask for an extra approval before it touches production. Written for every run whose
/// definition declares a <c>candidateVersion</c> parameter, from the release of that version in the run's own
/// project (never another project's), after every other variable source, so neither the pipeline, a library nor a
/// launch variable can claim a verdict the release does not carry.
/// <list type="bullet">
/// <item><see cref="StatusVariable"/>: <c>accepted</c> or <c>rejected</c> (the Gate's verdict), <c>unqualified</c>
/// (the release exists but carries no verdict), <c>unknown</c> (no such release in the project).</item>
/// <item><see cref="GradeVariable"/>: the sealed assurance grade, <c>A</c> to <c>F</c>; <c>none</c> when the release
/// carries no grade, <c>unknown</c> when there is no such release.</item>
/// <item><see cref="ApprovalVariable"/>: <c>not-required</c> only for an accepted release graded A to E, otherwise
/// <c>required</c>. The pipeline engine's conditions test one variable at a time (no <c>or</c>), so the rule
/// "rejected, or graded F, or not known" is computed here and the template tests
/// <c>ne(variables['RELEASE_GATE_APPROVAL'], 'not-required')</c>, which also holds when the variable is absent:
/// a backend that does not write it makes the approval required, never skipped.</item>
/// </list>
/// </summary>
internal static class PipelineReleaseGateVariables
{
    internal const string CandidateVersionParameter = "candidateVersion";
    internal const string StatusVariable = "RELEASE_GATE_STATUS";
    internal const string GradeVariable = "RELEASE_GATE_GRADE";
    internal const string ApprovalVariable = "RELEASE_GATE_APPROVAL";

    internal const string Accepted = "accepted";
    internal const string Rejected = "rejected";
    internal const string Unqualified = "unqualified";
    internal const string Unknown = "unknown";
    internal const string NoGrade = "none";
    internal const string ApprovalRequired = "required";
    internal const string ApprovalNotRequired = "not-required";

    /// <summary>Writes the three variables into <paramref name="resolved"/> when the run deploys a candidate
    /// release; leaves it untouched otherwise.</summary>
    public static async Task InjectAsync(
        IDictionary<string, string> resolved, PipelineYamlDefinition definition, int? projectId,
        IPipelineRepository repo, CancellationToken ct)
    {
        var parameterKey = PipelineCommandBuilder.ParameterKeyPrefix + CandidateVersionParameter;
        var declared = definition.Parameters.Any(parameter =>
            string.Equals(parameter.Name, CandidateVersionParameter, StringComparison.OrdinalIgnoreCase));
        var hasValue = resolved.TryGetValue(parameterKey, out var rawSelector);
        if (!declared && !hasValue) return;

        var selector = rawSelector?.Trim() ?? string.Empty;
        DeploymentGateRelease? release = null;
        if (projectId is { } id && selector.Length > 0
            && !selector.Contains("$(", StringComparison.Ordinal)
            && !selector.Contains("${{", StringComparison.Ordinal))
            release = await repo.FindDeploymentGateReleaseAsync(id, selector, ct).ConfigureAwait(false);
        Write(resolved, release);
    }

    internal static void Write(IDictionary<string, string> resolved, DeploymentGateRelease? release)
    {
        var status = release switch
        {
            null => Unknown,
            { Deployable: true } => Accepted,
            { Deployable: false } => Rejected,
            _ => Unqualified
        };
        var grade = release is null ? Unknown : release.AssuranceGrade?.ToString() ?? NoGrade;
        var deploysWithoutApproval = status == Accepted && release?.AssuranceGrade is { } sealedGrade
            && sealedGrade < AnalysisGrade.F;
        resolved[StatusVariable] = status;
        resolved[GradeVariable] = grade;
        resolved[ApprovalVariable] = deploysWithoutApproval ? ApprovalNotRequired : ApprovalRequired;
    }
}
