// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>A run task expected to publish analysis reports.</summary>
internal sealed record ExpectedAnalysisProducer(
    int Id,
    OperationKind Operation,
    string Command,
    string StepName);

/// <summary>One expected report that is absent, or an unclaimed DAST report. <paramref name="Token"/> is the entry listed
/// in <c>MissingProducers</c> (<c>key:task:id</c>, <c>coverage-task:id</c>, <c>unexpected-report:id</c>); the rest says what
/// it concerns so a verdict names it without taking the token apart (recette R-21). <paramref name="ReportId"/> is set only
/// for an unexpected report. <paramref name="TaskId"/> is the task that was to publish the missing report, so the verdict
/// can say what became of it; null for an unexpected report.</summary>
internal sealed record MissingAnalysisProducer(
    string Token,
    bool Unexpected,
    string Key,
    string? StepName,
    int? ReportId,
    int? TaskId = null);

/// <summary>Matches the reports a run published against the tasks that were expected to publish them,
/// extracted from <see cref="AnalysisInsightsRepository"/>: each report satisfies one producer at most,
/// and a DAST run admits no report nobody asked for.</summary>
internal static class AnalysisProducerMatcher
{
    /// <summary>One entry per expected report that is absent (<c>key:task:id</c>,
    /// <c>coverage-task:id</c>), plus <c>unexpected-report:id</c> for an unclaimed DAST report when the
    /// run holds a DAST scanner; each entry says what it concerns.</summary>
    public static List<MissingAnalysisProducer> FindMissing(
        IReadOnlyCollection<ExpectedAnalysisProducer> expectedProducers,
        IReadOnlyCollection<AnalysisRunGateReportDto> reports)
    {
        var missing = new List<MissingAnalysisProducer>();
        var remainingReports = reports.ToList();
        foreach (var producer in expectedProducers)
        {
            if (producer.Operation == OperationKind.PipelinePublishCoverage)
                ClaimCoverage(producer, remainingReports, missing);
            else
                ClaimKeyedReports(producer, remainingReports, missing);
        }
        if (expectedProducers.Any(IsDastScanner))
        {
            missing.AddRange(remainingReports
                .Where(report => report.Category == AnalysisCategory.Dast)
                .Select(report => new MissingAnalysisProducer(
                    $"unexpected-report:{report.ReportId}", true, report.ScannerKey, report.StepName, report.ReportId)));
        }
        return missing;
    }

    private static void ClaimCoverage(
        ExpectedAnalysisProducer producer, List<AnalysisRunGateReportDto> remainingReports, List<MissingAnalysisProducer> missing)
    {
        var coverage = remainingReports.FirstOrDefault(report => report.Category == AnalysisCategory.Coverage);
        if (coverage is null)
            missing.Add(new MissingAnalysisProducer($"coverage-task:{producer.Id}", false, "coverage", producer.StepName, null, producer.Id));
        else remainingReports.Remove(coverage);
    }

    private static void ClaimKeyedReports(
        ExpectedAnalysisProducer producer, List<AnalysisRunGateReportDto> remainingReports, List<MissingAnalysisProducer> missing)
    {
        var requiresExactStep = IsDastScanner(producer);
        foreach (var key in ExpectedKeys(producer))
        {
            var report = remainingReports.FirstOrDefault(item =>
                string.Equals(item.ScannerKey, key, StringComparison.OrdinalIgnoreCase)
                && (!requiresExactStep
                    || string.Equals(item.StepName, producer.StepName, StringComparison.Ordinal)));
            if (report is null)
                missing.Add(new MissingAnalysisProducer($"{key}:task:{producer.Id}", false, key, producer.StepName, null, producer.Id));
            else remainingReports.Remove(report);
        }
    }

    private static IReadOnlyList<string> ExpectedKeys(ExpectedAnalysisProducer producer) => producer.Operation switch
    {
        OperationKind.PipelineRunScanner => [producer.Command],
        OperationKind.PipelinePublishLint => ["pipeline-sarif-lint"],
        OperationKind.PipelinePublishComplexity =>
            ["roslyn-metrics", "dotnet-architecture-metrics", "dotnet-architecture"],
        OperationKind.PipelinePublishMutation => ["stryker-mutation"],
        _ => []
    };

    private static bool IsDastScanner(ExpectedAnalysisProducer producer) =>
        producer.Operation == OperationKind.PipelineRunScanner
        && string.Equals(
            ScannerManifestCatalog.Find(producer.Command)?.Category,
            nameof(AnalysisCategory.Dast),
            StringComparison.OrdinalIgnoreCase);
}
