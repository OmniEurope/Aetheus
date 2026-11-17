// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Recette R-21: the violation parser and the grade aggregation both drop, in silence, a stored snapshot that is
/// not valid JSON, so a grade could rest on fewer reports than the run holds and say nothing. This names those snapshots
/// (the report, its producer and step, the parser's message). It reads the same evidence the same way and changes no
/// verdict: the dropped evidence stays dropped, it is only no longer invisible.</summary>
internal static class AnalysisGateUnreadableEvidence
{
    private const string PolicyEvidence = "policy snapshot";
    private const string GradeEvidence = "grade snapshot";
    private static readonly JsonSerializerOptions GradeOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<AnalysisGateUnreadableEvidenceDto> Find(
        IEnumerable<AnalysisEvaluationSnapshot> snapshots,
        IReadOnlyList<AnalysisRunGateReportDto> reports)
    {
        var found = new List<AnalysisGateUnreadableEvidenceDto>();
        foreach (var snapshot in snapshots)
        {
            var report = reports.FirstOrDefault(item => item.ReportId == snapshot.ReportId);
            if (PolicyError(snapshot.SnapshotJson) is { } policyError)
                found.Add(Entry(snapshot.ReportId, report, PolicyEvidence, policyError));
            if (GradeError(snapshot.GradeSnapshotJson) is { } gradeError)
                found.Add(Entry(snapshot.ReportId, report, GradeEvidence, gradeError));
        }
        return found;
    }

    private static string? PolicyError(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return null;
        }
        catch (JsonException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>Null for a readable snapshot and for the empty one (<c>{}</c>: a report with no grading), which the
    /// aggregation skips on purpose.</summary>
    private static string? GradeError(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try
        {
            return JsonSerializer.Deserialize<AnalysisGradeSummaryDto>(json, GradeOptions) is null
                ? "the snapshot is the JSON value null."
                : null;
        }
        catch (JsonException exception)
        {
            return exception.Message;
        }
    }

    private static AnalysisGateUnreadableEvidenceDto Entry(
        int reportId, AnalysisRunGateReportDto? report, string evidence, string detail) => new()
        {
            ReportId = reportId,
            ScannerKey = report?.ScannerKey,
            StepName = report?.StepName,
            Evidence = evidence,
            Detail = detail
        };
}
