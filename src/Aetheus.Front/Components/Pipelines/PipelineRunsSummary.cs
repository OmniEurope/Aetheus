// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Summary figures use the grid's loaded runs, with null values left for the view to
/// localize. Grade and success summaries retain the corresponding run IDs for navigation.</summary>
public sealed record PipelineRunsSummary(
    AnalysisGrade? LatestGrade,
    int SucceededCount,
    int FinishedCount,
    TimeSpan? AverageSuccessDuration,
    DateTime? LastSuccessAt,
    int? LatestGradeRunId = null,
    int? LastSuccessRunId = null)
{
    public static PipelineRunsSummary Create(IReadOnlyList<PipelineRunDto> runs)
    {
        var finished = runs.Where(run => run.CompletedAt is not null).ToList();

        // Successful runs only: a run that failed after two minutes would drag the average down and
        // misrepresent what a normal execution of this pipeline costs.
        var successDurations = finished
            .Where(run => run.Status == PipelineStatus.Success)
            .Select(run => run.CompletedAt!.Value - run.StartedAt)
            .Where(duration => duration > TimeSpan.Zero)
            .ToList();
        var latestGraded = runs
            .OrderByDescending(run => run.StartedAt)
            .FirstOrDefault(run => run.GateGrade is not null);
        var lastSuccess = finished
            .Where(run => run.Status == PipelineStatus.Success)
            .OrderByDescending(run => run.CompletedAt)
            .FirstOrDefault();

        return new PipelineRunsSummary(
            LatestGrade: latestGraded?.GateGrade,
            SucceededCount: finished.Count(run => run.Status == PipelineStatus.Success),
            FinishedCount: finished.Count,
            AverageSuccessDuration: successDurations.Count == 0
                ? null
                : TimeSpan.FromSeconds(successDurations.Average(duration => duration.TotalSeconds)),
            LastSuccessAt: lastSuccess?.CompletedAt,
            LatestGradeRunId: latestGraded?.Id,
            LastSuccessRunId: lastSuccess?.Id);
    }

    public int? SuccessPercent => FinishedCount == 0 ? null : SucceededCount * 100 / FinishedCount;

    /// <summary>Success share of the finished runs, or <paramref name="notAvailable"/> when none finished.</summary>
    public string SuccessRateLabel(string notAvailable) => SuccessPercent is { } percent
        ? $"{percent}% ({SucceededCount}/{FinishedCount})"
        : notAvailable;

    /// <summary>Mean duration of the successful runs, or <paramref name="notAvailable"/> when none succeeded.</summary>
    public string AverageDurationLabel(string notAvailable) => AverageSuccessDuration is { } average
        ? FormatDuration(average)
        : notAvailable;

    /// <summary>Local time of the last success, or <paramref name="notAvailable"/> when there is none.</summary>
    public string LastSuccessLabel(string notAvailable) => LastSuccessAt is { } lastSuccess
        ? lastSuccess.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)
        : notAvailable;

    public static string FormatDuration(TimeSpan duration) => DisplayFormatting.Duration(duration);
}
