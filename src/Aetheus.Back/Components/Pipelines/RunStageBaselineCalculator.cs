// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>One timed step of a sampled successful run.</summary>
public sealed record StepTimingRow(
    int RunId,
    DateTime RunStartedAt,
    string StageName,
    string StepName,
    string? MatrixLeg,
    bool IsSystem,
    DateTime StartedAt,
    DateTime CompletedAt);

/// <summary>Computes per-stage and per-step average and last durations independently of
/// persistence, leaving sample selection to the query.</summary>
public static class RunStageBaselineCalculator
{
    public static RunStageBaselinesDto Compute(IReadOnlyCollection<StepTimingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var steps = rows
            .GroupBy(row => (Stage: row.StageName.ToUpperInvariant(), Step: row.StepName.ToUpperInvariant(),
                Leg: (row.MatrixLeg ?? string.Empty).Trim().ToUpperInvariant(), row.IsSystem))
            .Select(group =>
            {
                // One duration per run: a retried step keeps its latest attempt.
                var perRun = group
                    .GroupBy(row => row.RunId)
                    .Select(run => run.OrderByDescending(row => row.CompletedAt).First())
                    .OrderByDescending(row => row.RunStartedAt)
                    .ToList();
                var first = perRun[0];
                return new StepBaselineDto
                {
                    StageName = first.StageName,
                    StepName = first.StepName,
                    MatrixLeg = string.IsNullOrWhiteSpace(first.MatrixLeg) ? null : first.MatrixLeg.Trim(),
                    IsSystem = first.IsSystem,
                    AverageSeconds = perRun.Average(Seconds),
                    LastSeconds = Seconds(first),
                    Samples = perRun.Count
                };
            })
            .OrderBy(step => step.StageName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(step => step.StepName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A stage lasts from its first step's start to its last step's end in that run, which is what
        // the progression bar draws; summing its steps would double-count parallel ones.
        var stages = rows
            .GroupBy(row => (Stage: row.StageName.ToUpperInvariant(), row.IsSystem))
            .Select(group =>
            {
                var perRun = group
                    .GroupBy(row => row.RunId)
                    .Select(run => (
                        RunStartedAt: run.First().RunStartedAt,
                        Seconds: (run.Max(row => row.CompletedAt) - run.Min(row => row.StartedAt)).TotalSeconds))
                    .OrderByDescending(run => run.RunStartedAt)
                    .ToList();
                return new StageBaselineDto
                {
                    StageName = group.First().StageName,
                    IsSystem = group.Key.IsSystem,
                    AverageSeconds = perRun.Average(run => Math.Max(0, run.Seconds)),
                    LastSeconds = Math.Max(0, perRun[0].Seconds),
                    Samples = perRun.Count
                };
            })
            .OrderBy(stage => stage.StageName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RunStageBaselinesDto
        {
            SampleRuns = rows.Select(row => row.RunId).Distinct().Count(),
            Stages = stages,
            Steps = steps
        };
    }

    private static double Seconds(StepTimingRow row) => Math.Max(0, (row.CompletedAt - row.StartedAt).TotalSeconds);
}
