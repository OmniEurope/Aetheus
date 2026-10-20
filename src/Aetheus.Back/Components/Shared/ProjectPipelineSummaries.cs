// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Shared;

/// <summary>Read only pipeline counts and last-run summary columns for project tiles. Supply
/// untracked partial Pipeline instances to ProjectDtoMapper without loading YAML
/// definitions.</summary>
internal static class ProjectPipelineSummaries
{
    /// <param name="withLastRun">Also reads each pipeline's latest run (status and start).</param>
    internal static async Task AttachAsync(
        AppDbContext db, IReadOnlyList<Project> projects, bool withLastRun, CancellationToken ct)
    {
        if (projects.Count == 0) return;
        var projectIds = projects.Select(project => project.Id).ToList();
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.ProjectId != null && projectIds.Contains(pipeline.ProjectId.Value))
            .Select(pipeline => new
            {
                pipeline.Id,
                ProjectId = pipeline.ProjectId!.Value,
                LastRun = withLastRun
                    ? pipeline.Runs.OrderByDescending(run => run.StartedAt)
                        .Select(run => new { run.Id, run.Status, run.StartedAt })
                        .FirstOrDefault()
                    : null
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var byProject = pipelines.ToLookup(pipeline => pipeline.ProjectId);
        foreach (var project in projects)
            project.Pipelines = byProject[project.Id]
                .Select(pipeline => new Pipeline
                {
                    Id = pipeline.Id,
                    ProjectId = pipeline.ProjectId,
                    Runs = pipeline.LastRun is null
                        ? []
                        : [new PipelineRun
                        {
                            Id = pipeline.LastRun.Id,
                            PipelineId = pipeline.Id,
                            Status = pipeline.LastRun.Status,
                            StartedAt = pipeline.LastRun.StartedAt
                        }]
                })
                .ToList();
    }
}
