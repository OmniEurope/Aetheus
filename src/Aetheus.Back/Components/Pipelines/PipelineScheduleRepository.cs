// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineScheduleRepository(AppDbContext db) : IPipelineScheduleRepository
{
    public const int AverageSampleSize = 10;

    // The owner chain is the one of PipelineOrganizationScope.InOrganizations, written as a projection.
    public Task<PipelineScheduleTargetRow?> GetTargetAsync(int pipelineId, CancellationToken ct) =>
        db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.Id == pipelineId)
            .Select(pipeline => new PipelineScheduleTargetRow(
                pipeline.Id,
                pipeline.Name,
                pipeline.YamlDefinition,
                pipeline.Project != null
                    ? pipeline.Project.OrganizationId
                    : pipeline.Environment != null && pipeline.Environment.Project != null
                        ? pipeline.Environment.Project.OrganizationId
                        : pipeline.ProjectServer != null ? pipeline.ProjectServer.Project.OrganizationId : 0))
            .FirstOrDefaultAsync(ct);

    public Task<List<PipelineScheduleRow>> GetCandidatesAsync(
        int organizationId, IReadOnlyCollection<int>? accessiblePipelineIds, CancellationToken ct)
    {
        var pipelines = db.Pipelines.AsNoTracking().InOrganizations([organizationId]);
        if (accessiblePipelineIds is not null)
        {
            pipelines = pipelines.Where(pipeline => accessiblePipelineIds.Contains(pipeline.Id));
        }

        return pipelines
            .Where(pipeline => pipeline.TriggerType == PipelineTriggerType.Schedule
                || (pipeline.TriggerType == PipelineTriggerType.Manual && pipeline.YamlDefinition.Contains("schedule")))
            .OrderBy(pipeline => pipeline.Name)
            .ThenBy(pipeline => pipeline.Id)
            .Select(pipeline => new PipelineScheduleRow(
                pipeline.Id, pipeline.Name, pipeline.TriggerType, pipeline.YamlDefinition,
                pipeline.ProjectId, pipeline.SourceRepositoryId, pipeline.SourceBranch))
            .ToListAsync(ct);
    }

    // Runners the scheduler can use in this organization (the same switch as PipelineServerResolver); a plan with
    // none still runs one thing at a time, so the capacity is never below one.
    public async Task<int> GetExecutionCapacityAsync(int organizationId, CancellationToken ct) =>
        Math.Max(1, await db.Servers.AsNoTracking()
            .CountAsync(server => server.OrganizationId == organizationId && server.PipelineRunnerEnabled, ct).ConfigureAwait(false));

    public async Task<Dictionary<int, ScheduleLastRunDto>> GetLastRunsAsync(IReadOnlyCollection<int> pipelineIds, CancellationToken ct = default)
    {
        if (pipelineIds.Count == 0)
        {
            return [];
        }

        var ids = pipelineIds.ToList();
        var rows = await db.Pipelines
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => db.PipelineRuns
                .Where(r => r.PipelineId == p.Id)
                .OrderByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.Id)
                .Select(r => new LastRunRow(
                    p.Id, r.Id, r.BuildNumber, r.Status, r.StartedAt, r.CompletedAt,
                    db.AnalysisEvaluations
                        .Where(e => e.PipelineRunId == r.Id)
                        .OrderByDescending(e => e.EvaluatedAt)
                        .ThenByDescending(e => e.Id)
                        .Select(e => (AnalysisGateStatus?)e.Status)
                        .FirstOrDefault(),
                    db.AnalysisEvaluations
                        .Where(e => e.PipelineRunId == r.Id)
                        .OrderByDescending(e => e.EvaluatedAt)
                        .ThenByDescending(e => e.Id)
                        .Select(e => e.Grade)
                        .FirstOrDefault()))
                .FirstOrDefault())
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Where(row => row is not null)
            .ToDictionary(
                row => row!.PipelineId,
                row => new ScheduleLastRunDto
                {
                    RunId = row!.RunId,
                    BuildNumber = row.BuildNumber,
                    Status = row.Status,
                    StartedAt = row.StartedAt,
                    DurationMinutes = row.CompletedAt is { } completed
                        ? Math.Max(1, (int)Math.Ceiling((completed - row.StartedAt).TotalMinutes))
                        : null,
                    GateStatus = row.GateStatus,
                    Grade = row.Grade
                });
    }

    private sealed record LastRunRow(
        int PipelineId, int RunId, int BuildNumber, PipelineStatus Status, DateTime StartedAt, DateTime? CompletedAt,
        AnalysisGateStatus? GateStatus, AnalysisGrade? Grade);

    public async Task<Dictionary<int, int>> GetAverageMinutesAsync(IReadOnlyCollection<int> pipelineIds, CancellationToken ct = default)
    {
        if (pipelineIds.Count == 0)
        {
            return [];
        }

        var ids = pipelineIds.ToList();
        var rows = await db.Pipelines
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                Runs = db.PipelineRuns
                    .Where(r => r.PipelineId == p.Id && r.Status == PipelineStatus.Success && r.CompletedAt != null)
                    .OrderByDescending(r => r.CompletedAt)
                    .ThenByDescending(r => r.Id)
                    .Take(AverageSampleSize)
                    .Select(r => new { r.StartedAt, CompletedAt = r.CompletedAt!.Value })
                    .ToList()
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Where(row => row.Runs.Count > 0)
            .ToDictionary(
                row => row.Id,
                row => Math.Max(1, (int)Math.Ceiling(row.Runs.Average(run => (run.CompletedAt - run.StartedAt).TotalMinutes))));
    }
}
