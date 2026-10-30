// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>Shares domain-by-domain run-tree grading between the backend and Gate tab, retaining
/// the worst grade measured for each domain.</summary> <remarks>Snapshot hashing remains the
/// backend responsibility; the merged display result has no <see
/// cref="AnalysisGradeSummaryDto.SnapshotHash"/>.</remarks>
public static class AnalysisGradeMerge
{
    public static AnalysisGradeSummaryDto MergeRuns(
        IReadOnlyCollection<AnalysisGradeSummaryDto> runs,
        int pipelineRunId)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var domains = Enum.GetValues<AnalysisGradeDomain>()
            .Select(domain => MergeDomain(runs
                .SelectMany(run => run.Domains)
                .Where(item => item.Domain == domain)
                .ToList()))
            .OfType<AnalysisGradeDomainDto>()
            .ToList();
        var minimumGrade = runs
            .Where(run => run.MinimumGrade.HasValue)
            .Select(run => run.MinimumGrade)
            .Min();
        var commits = runs
            .Select(run => run.CommitHash)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var summary = Summarize(
            domains,
            minimumGrade,
            commits.Count == 1 ? commits[0] : null,
            pipelineRunId,
            runs.Max(run => run.EvaluatedAt));
        // Runs on several commits: the commit the grade was measured on is the limiting domain's.
        if (summary.CommitHash is null && summary.LimitingDomain is { } limiting)
            summary = summary with { CommitHash = domains.First(domain => domain.Domain == limiting).CommitHash };
        return summary;
    }

    /// <summary>The overall grade is the worst required domain that has a grade (recette R-73, R-74: a grade is
    /// always shown, from what was observed). <see cref="AnalysisGradeSummaryDto.Completeness"/> only records
    /// whether every required domain was measured completely; a required measure nobody observed is listed on
    /// its domain, never turned into a grade nor a state of the grade.</summary>
    public static AnalysisGradeSummaryDto Summarize(
        IReadOnlyList<AnalysisGradeDomainDto> domains,
        AnalysisGrade? minimumGrade,
        string? commitHash,
        int? pipelineRunId,
        DateTime? evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(domains);
        var required = domains.Where(domain => domain.Required).ToList();
        var complete = required.Count > 0
            && required.All(domain => domain.Completeness == AnalysisGradeCompleteness.Complete
                && domain.Grade.HasValue);
        var grade = required.Where(domain => domain.Grade.HasValue).Max(domain => domain.Grade);
        return new AnalysisGradeSummaryDto
        {
            OverallGrade = grade,
            MinimumGrade = minimumGrade,
            Completeness = complete
                ? AnalysisGradeCompleteness.Complete
                : AnalysisGradeCompleteness.Incomplete,
            LimitingDomain = grade.HasValue
                ? required.First(domain => domain.Grade == grade).Domain
                : null,
            CommitHash = commitHash,
            PipelineRunId = pipelineRunId,
            EvaluatedAt = evaluatedAt,
            Domains = domains
        };
    }

    private static AnalysisGradeDomainDto? MergeDomain(IReadOnlyCollection<AnalysisGradeDomainDto> candidates)
    {
        if (candidates.Count == 0) return null;
        // A domain measured in part still has its grade, from the measures observed (recette R-74).
        var measured = candidates
            .Where(domain => domain.Grade.HasValue)
            .ToList();
        var chosen = measured.Count > 0
            ? measured.OrderByDescending(domain => domain.Grade).ThenByDescending(domain => domain.EvaluatedAt).First()
            : candidates.OrderByDescending(domain => domain.Measures.Count).ThenByDescending(domain => domain.EvaluatedAt).First();
        return chosen with { Required = candidates.Any(domain => domain.Required) };
    }
}
