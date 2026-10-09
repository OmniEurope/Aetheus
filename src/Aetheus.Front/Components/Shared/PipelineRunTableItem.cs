// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public sealed record PipelineRunTableItem
{
    public int RunId { get; init; }
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public int? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public PipelineStatus Status { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ServerName { get; init; }
    public string? ServerOs { get; init; }
    public string? CurrentStep { get; init; }
    public string? BranchName { get; init; }
    public string? CommitHash { get; init; }

    /// <summary>Recette R-373: the run's commit as Aetheus recorded it, for the link to its page.</summary>
    public int? CommitId { get; init; }
    public string? RepositoryUrl { get; init; }

    /// <summary>Recette R-373: the internal repository the run built, when the backend resolved it.</summary>
    public int? RepositoryId { get; init; }
    public AnalysisGrade? GateGrade { get; init; }
    public bool CancellationRequested { get; init; }
    public DateTime LatestStartedAt { get; init; }
    public IReadOnlyList<int> TriggeredRunIds { get; init; } = [];
    public IReadOnlyList<PipelineRunTableItem> LinkedRuns { get; init; } = [];

    public static PipelineRunTableItem FromRun(PipelineRunDto run) => new()
    {
        RunId = run.Id,
        PipelineId = run.PipelineId,
        PipelineName = run.PipelineName,
        ProjectId = run.ProjectId,
        ProjectName = run.ProjectName,
        Status = run.Status,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        ServerName = run.Steps.FirstOrDefault(step => !step.IsSystem && step.ServerId.HasValue)?.ServerName,
        ServerOs = run.Steps.FirstOrDefault(step => !step.IsSystem && step.ServerId.HasValue)?.ServerOs,
        CurrentStep = PipelineHelper.GetCurrentStepLabel(run),
        BranchName = run.BranchName,
        CommitHash = run.CommitHash,
        CommitId = (run.Commits.FirstOrDefault(commit => string.Equals(commit.Sha, run.CommitHash, StringComparison.OrdinalIgnoreCase))
            ?? run.Commits.FirstOrDefault())?.Id,
        RepositoryUrl = run.RepositoryUrl,
        RepositoryId = run.RepositoryId,
        GateGrade = run.GateGrade,
        CancellationRequested = run.CancellationRequested,
        LatestStartedAt = run.StartedAt,
        TriggeredRunIds = run.Steps
            .Where(step => step.TriggeredRunId.HasValue)
            .Select(step => step.TriggeredRunId!.Value)
            .Distinct()
            .ToList()
    };

    public static IReadOnlyList<PipelineRunTableItem> GroupRuns(IEnumerable<PipelineRunDto> runs)
    {
        var items = runs
            .Select(FromRun)
            .DistinctBy(run => run.RunId)
            .ToList();
        var byId = items.ToDictionary(run => run.RunId);
        var parentByChild = items
            .SelectMany(parent => parent.TriggeredRunIds
                .Where(byId.ContainsKey)
                .Select(childRunId => new { ChildRunId = childRunId, ParentRunId = parent.RunId }))
            .GroupBy(link => link.ChildRunId)
            .ToDictionary(group => group.Key, group => group.First().ParentRunId);

        return items
            .GroupBy(run => ResolveRootRunId(run.RunId, parentByChild))
            .Select(group =>
            {
                var groupRunIds = group.Select(run => run.RunId).ToHashSet();
                return BuildTree(group.Key, byId, parentByChild, groupRunIds, []);
            })
            // Recette R2-102: what still runs or awaits an approval leads, most recently started first,
            // then the rest by date: a deployment started before newer, finished runs no longer sinks
            // under them. A column sort chosen in the grid still reorders the rows.
            .OrderByDescending(run => !run.Status.IsTerminal())
            .ThenByDescending(run => run.LatestStartedAt)
            .ThenByDescending(run => run.RunId)
            .ToList();
    }

    private static PipelineRunTableItem BuildTree(
        int runId,
        IReadOnlyDictionary<int, PipelineRunTableItem> byId,
        IReadOnlyDictionary<int, int> parentByChild,
        IReadOnlySet<int> groupRunIds,
        HashSet<int> path)
    {
        var run = byId[runId];
        if (!path.Add(runId))
            return run;

        var linkedRuns = run.TriggeredRunIds
            .Where(childRunId =>
                groupRunIds.Contains(childRunId)
                && parentByChild.GetValueOrDefault(childRunId) == runId
                && !path.Contains(childRunId))
            .Select(childRunId => BuildTree(childRunId, byId, parentByChild, groupRunIds, path))
            .ToList();
        path.Remove(runId);

        return run with
        {
            LinkedRuns = linkedRuns,
            LatestStartedAt = linkedRuns
                .Select(child => child.LatestStartedAt)
                .Append(run.StartedAt)
                .Max()
        };
    }

    private static int ResolveRootRunId(int runId, IReadOnlyDictionary<int, int> parentByChild)
    {
        var currentRunId = runId;
        var visited = new HashSet<int> { runId };

        while (parentByChild.TryGetValue(currentRunId, out var parentRunId))
        {
            if (!visited.Add(parentRunId))
                return visited.Min();
            currentRunId = parentRunId;
        }

        return currentRunId;
    }
}
