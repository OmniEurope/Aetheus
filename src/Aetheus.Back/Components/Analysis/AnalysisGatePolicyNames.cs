// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Recette R-17: the gate's violations come out of the evaluation snapshots, which carry a policy by its key
/// (<c>policy:{key}:{scope}:v{version}</c>) and nothing a person can read. This joins each violation to the
/// policy it came from so the run page can name the rule by its <see cref="AnalysisPolicy.Name"/>.
/// </summary>
internal static class AnalysisGatePolicyNames
{
    /// <summary>The keys the violations name, to look the policies up by.</summary>
    public static IReadOnlyList<string> KeysOf(IEnumerable<AnalysisRunGateViolationDto> violations) =>
        [.. violations.Select(violation => violation.PolicyKey).Distinct(StringComparer.Ordinal)];

    /// <summary>The violations with the name of their policy. <paramref name="candidates"/> are the stored policies
    /// of the run's organization and project that share a violation's key, plus the built-in catalog; among
    /// them the policy of the same version wins, then the most specific (project, organization, global), then the
    /// latest version. A violation with no policy, or an unnamed one, keeps a null name.</summary>
    public static IReadOnlyList<AnalysisRunGateViolationDto> Apply(
        IReadOnlyList<AnalysisRunGateViolationDto> violations,
        IReadOnlyCollection<AnalysisPolicy> candidates) =>
        [.. violations.Select(violation => violation with { PolicyName = NameOf(violation, candidates) })];

    private static string? NameOf(AnalysisRunGateViolationDto violation, IReadOnlyCollection<AnalysisPolicy> candidates)
    {
        var policy = candidates
            .Where(candidate => string.Equals(AnalysisPolicyKey.ForExisting(candidate), violation.PolicyKey, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Version == violation.Version)
            .ThenByDescending(candidate => Specificity(candidate))
            .ThenByDescending(candidate => candidate.Version)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(policy?.Name) ? null : policy.Name.Trim();
    }

    private static int Specificity(AnalysisPolicy policy) =>
        policy.ProjectId is not null ? 2 : policy.OrganizationId is not null ? 1 : 0;
}
