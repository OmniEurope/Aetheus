// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// The verdict of a run's gate and the reasons behind an Error one, extracted from <see cref="AnalysisInsightsRepository"/>.
/// Recette R-21: the verdict is decided from the causes, so no gate is in Error without a cause that names the tool, the
/// step, the report and the reason (run 3100 of the nightly read "Error, 9 reports, 0 findings, grade A" and named nothing).
/// </summary>
internal static class AnalysisGateVerdict
{
    /// <summary>Every condition that makes the gate an operational error: no report, a missing or unexpected producer, a
    /// report the tool ended in error, timed out or could not run, a report with no evaluation, a grading that measured
    /// nothing. This is exactly the set of conditions of the former <c>ResolveGateStatus</c>: a report that failed still
    /// makes the gate an Error when a later report of the same step succeeded (no retry exemption).</summary>
    public static IReadOnlyList<AnalysisGateErrorCauseDto> ErrorCauses(
        int runId,
        IReadOnlyList<AnalysisRunGateReportDto> reports,
        IReadOnlyList<MissingAnalysisProducer> missingProducers,
        AnalysisGradeSummaryDto? grade,
        IReadOnlyDictionary<int, string>? producerTaskDetails = null)
    {
        var causes = new List<AnalysisGateErrorCauseDto>();
        if (reports.Count == 0)
            causes.Add(new AnalysisGateErrorCauseDto { Kind = AnalysisGateErrorCauseKind.NoReport, PipelineRunId = runId });
        causes.AddRange(missingProducers.Select(missing => new AnalysisGateErrorCauseDto
        {
            Kind = missing.Unexpected ? AnalysisGateErrorCauseKind.UnexpectedReport : AnalysisGateErrorCauseKind.MissingProducer,
            PipelineRunId = runId,
            ScannerKey = missing.Key,
            StepName = missing.StepName,
            ReportId = missing.ReportId,
            // What became of the task that was to publish the report: its end and its last error lines.
            Detail = missing.TaskId is { } taskId && producerTaskDetails is not null
                && producerTaskDetails.TryGetValue(taskId, out var taskDetail)
                ? taskDetail
                : null
        }));
        causes.AddRange(reports
            .Where(IsFailing)
            .Select(report => ReportCause(runId, report)));
        if (grade is { OverallGrade: null, Completeness: AnalysisGradeCompleteness.Incomplete })
            causes.Add(new AnalysisGateErrorCauseDto
            {
                Kind = AnalysisGateErrorCauseKind.GradeNotComputed,
                PipelineRunId = runId,
                Detail = UnobservedMeasures(grade)
            });
        return causes;
    }

    private const int MaxNamedMeasures = 6;

    /// <summary>The required grading rules nobody observed, each with the step that should have produced its evidence, so
    /// "no measure could be graded" names what was not measured. Null when the grade lists no such rule.</summary>
    private static string? UnobservedMeasures(AnalysisGradeSummaryDto grade)
    {
        var unobserved = grade.Domains
            .SelectMany(domain => domain.Measures)
            .Where(measure => measure.Required && !(measure.Observed && measure.Grade.HasValue))
            .Select(measure => string.IsNullOrWhiteSpace(measure.ProducerStep)
                ? $"règle {measure.Key}"
                : $"règle {measure.Key} (étape « {measure.ProducerStep} »)")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unobserved.Count == 0) return null;
        var shown = string.Join(", ", unobserved.Take(MaxNamedMeasures));
        return unobserved.Count > MaxNamedMeasures
            ? $"mesures non observées : {shown} et {unobserved.Count - MaxNamedMeasures} autres"
            : $"mesures non observées : {shown}";
    }

    /// <summary>The run's gate verdict: an operational error (the causes above) wins over a block, a block (a blocking
    /// report or a grade below the minimum) over a warning. A grading rule the validator rejects is no operational error: it
    /// is a measure graded F (run #2958), so it blocks through the minimum grade like any other F. A grade resting on part
    /// of the required measures is still a grade (recette R-73, R-74): the measures nobody observed are listed on the Gate
    /// tab with their step, and a producer that ran and published nothing is already a missing producer.</summary>
    public static AnalysisGateStatus Resolve(
        IReadOnlyList<AnalysisRunGateReportDto> reports,
        IReadOnlyList<AnalysisGateErrorCauseDto> errorCauses,
        AnalysisGradeSummaryDto? grade)
    {
        if (errorCauses.Count > 0) return AnalysisGateStatus.Error;
        var gradeBlocks = grade?.OverallGrade.HasValue == true
            && (grade.MinimumGrade.HasValue
                ? grade.OverallGrade.Value > grade.MinimumGrade.Value
                // Only a rejected minimum_grade leaves a graded summary without a minimum: its F cannot pass.
                : grade.OverallGrade.Value == AnalysisGrade.F);
        if (gradeBlocks || reports.Any(report => report.GateStatus == AnalysisGateStatus.Blocked))
            return AnalysisGateStatus.Blocked;
        return reports.Any(report => report.GateStatus == AnalysisGateStatus.Warning)
            ? AnalysisGateStatus.Warning
            : AnalysisGateStatus.Passed;
    }

    private static bool IsFailing(AnalysisRunGateReportDto report) =>
        report.ReportStatus is AnalysisReportStatus.Error or AnalysisReportStatus.TimedOut or AnalysisReportStatus.Unavailable
        || report.GateStatus is null or AnalysisGateStatus.Error;

    private static AnalysisGateErrorCauseDto ReportCause(int runId, AnalysisRunGateReportDto report) => new()
    {
        Kind = report.ReportStatus switch
        {
            AnalysisReportStatus.Error => AnalysisGateErrorCauseKind.ReportError,
            AnalysisReportStatus.TimedOut => AnalysisGateErrorCauseKind.ReportTimedOut,
            AnalysisReportStatus.Unavailable => AnalysisGateErrorCauseKind.ReportUnavailable,
            _ => report.GateStatus is null
                ? AnalysisGateErrorCauseKind.NoEvaluation
                : AnalysisGateErrorCauseKind.EvaluationError
        },
        PipelineRunId = runId,
        ScannerKey = report.ScannerKey,
        ScannerName = report.ScannerName,
        StepName = report.StepName,
        ReportId = report.ReportId,
        PipelineArtifactId = report.PipelineArtifactId,
        ReportPath = report.ReportPath,
        Detail = report.ErrorMessage
    };
}
