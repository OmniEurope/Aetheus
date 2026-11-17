// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-21: the lines the Gate tab shows under an Error verdict, one per reason, each naming the producer (tool), the
/// step, the report with its artifact and file, and the precise reason, in the user's language. Run 3100 of the nightly
/// showed "Error" beside a grade A with no finding and nothing that said which producer or report was refused.
/// </summary>
internal static class PipelineRunGateErrorCauses
{
    /// <summary>One readable line per cause of an Error gate; empty for any other verdict.</summary>
    public static IReadOnlyList<string> Lines(AnalysisRunGateDto gate, IStringLocalizer<AppStrings> localizer)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(localizer);
        return gate.Status == AnalysisGateStatus.Error ? [.. gate.ErrorCauses.Select(cause => Line(cause, localizer))] : [];
    }

    private static string Line(AnalysisGateErrorCauseDto cause, IStringLocalizer<AppStrings> localizer)
    {
        var line = string.Format(CultureInfo.CurrentCulture, localizer[ReasonKey(cause.Kind)].Value, Subject(cause, localizer), cause.Detail);
        // The Dependency-Track state is part of its sentence; any other detail is the producer's own message.
        return string.IsNullOrWhiteSpace(cause.Detail) || cause.Kind == AnalysisGateErrorCauseKind.DependencyTrack
            ? line
            : $"{line} {string.Format(CultureInfo.CurrentCulture, localizer["GateErrorCauseDetail"].Value, cause.Detail)}";
    }

    /// <summary>What the cause concerns: the tool, its step, the report with its artifact and file, the run.</summary>
    private static string Subject(AnalysisGateErrorCauseDto cause, IStringLocalizer<AppStrings> localizer)
    {
        var tool = !string.IsNullOrWhiteSpace(cause.ScannerName) && !string.Equals(cause.ScannerName, cause.ScannerKey, StringComparison.OrdinalIgnoreCase)
            ? $"{cause.ScannerName} ({cause.ScannerKey})"
            : cause.ScannerKey ?? cause.ScannerName;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(tool)) parts.Add(tool);
        if (!string.IsNullOrWhiteSpace(cause.StepName)) parts.Add(string.Format(CultureInfo.CurrentCulture, localizer["GateErrorCauseStep"].Value, cause.StepName));
        if (cause.ReportId is { } report) parts.Add(string.Format(CultureInfo.CurrentCulture, localizer["GateErrorCauseReport"].Value, report));
        if (cause.PipelineArtifactId is { } artifact) parts.Add(string.Format(CultureInfo.CurrentCulture, localizer["GateErrorCauseArtifact"].Value, artifact));
        if (!string.IsNullOrWhiteSpace(cause.ReportPath)) parts.Add(string.Format(CultureInfo.CurrentCulture, localizer["GateErrorCauseFile"].Value, cause.ReportPath));
        if (cause.PipelineRunId > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, localizer["GateErrorCauseRun"].Value, cause.PipelineRunId));
        return string.Join(", ", parts);
    }

    private static string ReasonKey(AnalysisGateErrorCauseKind kind) => kind switch
    {
        AnalysisGateErrorCauseKind.NoReport => "GateErrorCauseNoReport",
        AnalysisGateErrorCauseKind.ReportError => "GateErrorCauseReportError",
        AnalysisGateErrorCauseKind.ReportTimedOut => "GateErrorCauseReportTimedOut",
        AnalysisGateErrorCauseKind.ReportUnavailable => "GateErrorCauseReportUnavailable",
        AnalysisGateErrorCauseKind.NoEvaluation => "GateErrorCauseNoEvaluation",
        AnalysisGateErrorCauseKind.EvaluationError => "GateErrorCauseEvaluationError",
        AnalysisGateErrorCauseKind.MissingProducer => "GateErrorCauseMissingProducer",
        AnalysisGateErrorCauseKind.UnexpectedReport => "GateErrorCauseUnexpectedReport",
        AnalysisGateErrorCauseKind.DependencyTrack => "GateErrorCauseDependencyTrack",
        AnalysisGateErrorCauseKind.GradeNotComputed => "GateErrorCauseGradeNotComputed",
        _ => "GateErrorCauseUnknown"
    };
}
