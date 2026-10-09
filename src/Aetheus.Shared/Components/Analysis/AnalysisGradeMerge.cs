// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// One rule for grading a run tree, shared by the backend (project summary) and the run's Gate tab so
/// the two can never show different letters for the same runs. Recette R2-118: the runs merge domain by
/// domain, each domain keeping the worst grade among the runs that measured it completely, so a run that
/// does not grade a domain (the quality run has no security rule) no longer hides the run that does.
/// Recette R2-129: the Gate tab of candidate #2571 showed the quality run's breakdown alone, with an
/// incomplete security domain, while the security run had graded it A.
/// </summary>
/// <remarks>The result carries no <see cref="AnalysisGradeSummaryDto.SnapshotHash"/>: hashing the
/// snapshot is the backend's concern, the Gate tab only displays the merge.</remarks>
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

    /// <summary>The overall grade is the worst required domain, and only once every required domain is
    /// completely measured; otherwise the summary is incomplete and ungraded.</summary>
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
        var grade = complete ? required.Max(domain => domain.Grade) : null;
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
        var measured = candidates
            .Where(domain => domain.Completeness == AnalysisGradeCompleteness.Complete && domain.Grade.HasValue)
            .ToList();
        var chosen = measured.Count > 0
            ? measured.OrderByDescending(domain => domain.Grade).ThenByDescending(domain => domain.EvaluatedAt).First()
            : candidates.OrderByDescending(domain => domain.Measures.Count).ThenByDescending(domain => domain.EvaluatedAt).First();
        return chosen with { Required = candidates.Any(domain => domain.Required) };
    }
}
