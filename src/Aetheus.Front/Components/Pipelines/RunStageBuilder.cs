// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Groups a run's step runs into <see cref="StageViewModel"/>s (one per stage), in step order. Shared by
/// the run page's own timeline and the recursive <c>RunTimelineTree</c> so a nested child run is grouped
/// exactly like the top-level run.
/// </summary>
internal static class RunStageBuilder
{
    public static List<StageViewModel> Build(PipelineRunDto? run)
    {
        if (run is null) return [];
        var stages = run.Steps
            .GroupBy(s => s.StageName)
            .Select(g =>
            {
                var steps = g.OrderBy(s => s.Id).ToList();
                var firstStart = steps.Where(s => s.StartedAt.HasValue).Select(s => s.StartedAt).Min();
                var lastEnd = steps.All(s => s.CompletedAt.HasValue) ? steps.Max(s => s.CompletedAt) : null;
                var isSystem = steps.All(s => s.IsSystem);
                var displayName = g.Key.StartsWith("System:", StringComparison.Ordinal)
                    ? g.Key.Replace("System:", "", StringComparison.Ordinal)
                    : g.Key;
                return new StageViewModel
                {
                    Name = g.Key,
                    DisplayName = displayName,
                    Steps = steps,
                    Status = PipelineRunFormatting.GetAggregateStatus(steps),
                    StartedAt = firstStart,
                    CompletedAt = lastEnd,
                    IsSystem = isSystem,
                    GroupName = steps.FirstOrDefault()?.GroupName,
                    Depth = steps.Select(step => step.StageDepth).FirstOrDefault(depth => depth is not null),
                    SkippedCondition = PipelineRunFormatting.GetSkippedCondition(steps),
                    IsRejected = PipelineRunFormatting.IsRejectedStage(steps)
                };
            })
            .ToList();

        // System:Prepare and System:Cleanup are injected around the graph by the run launcher, not
        // declared in the YAML, so they carry no depth at all. Sorting "no depth" after the graph put
        // the clone that runs FIRST at the bottom of the page, green under stages still running - the
        // very thing the depth order exists to stop. They keep the ends the launcher gave them.
        var firstGraphStepId = stages
            .Where(stage => !stage.IsSystem && stage.Steps.Count > 0)
            .Select(stage => stage.Steps[0].Id)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        return stages
            .OrderBy(stage => SystemBand(stage, firstGraphStepId))
            // Depth first, definition order within a depth. Definition order alone put stages that
            // run CONCURRENTLY in an arbitrary vertical sequence, so one turned green above another
            // that was still running and the page implied an ordering the run never had. Stages
            // without a depth (no usable snapshot) keep their definition order, after the graph.
            .ThenBy(stage => stage.Depth ?? int.MaxValue)
            .ThenBy(stage => stage.Steps.Count > 0 ? stage.Steps[0].Id : int.MaxValue)
            .ToList();
    }

    /// <summary>-1 for a system stage injected before the graph, 0 for the graph itself, 1 for a
    /// system stage injected after it. Read from the step order the launcher assigned, so it holds
    /// for any injected stage rather than only the two named ones.</summary>
    private static int SystemBand(StageViewModel stage, int firstGraphStepId) =>
        !stage.IsSystem ? 0
        : stage.Steps.Count > 0 && stage.Steps[0].Id < firstGraphStepId ? -1
        : 1;
}
