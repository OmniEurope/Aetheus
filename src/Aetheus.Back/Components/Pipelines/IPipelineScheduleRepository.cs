// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What the schedule plan needs of a pipeline, without the columns it does not. <paramref name="ProjectId"/>,
/// <paramref name="SourceRepositoryId"/> and <paramref name="SourceBranch"/> say where its definition is read from: the
/// project's repository on that branch (null: the repository's default branch), recette R-5; a pipeline of no project
/// has no repository to read.</summary>
internal sealed record PipelineScheduleRow(
    int Id, string Name, PipelineTriggerType TriggerType, string YamlDefinition,
    int? ProjectId = null, int? SourceRepositoryId = null, string? SourceBranch = null);

/// <summary>A pipeline whose schedule is previewed: its YAML, and the organization its project (or its
/// environment's or server's project) belongs to, 0 when it has none.</summary>
internal sealed record PipelineScheduleTargetRow(int Id, string Name, string YamlDefinition, int OrganizationId);

internal interface IPipelineScheduleRepository
{
    /// <summary>The pipeline, whether or not it is scheduled; null when it does not exist.</summary>
    Task<PipelineScheduleTargetRow?> GetTargetAsync(int pipelineId, CancellationToken ct);

    /// <summary>The organization's pipelines the engine schedules, plus the manual ones whose YAML may
    /// keep a <c>schedule:</c> value: the caller reads the YAML to tell. A null
    /// <paramref name="accessiblePipelineIds"/> means no per-pipeline restriction (a site-wide Admin).</summary>
    Task<List<PipelineScheduleRow>> GetCandidatesAsync(
        int organizationId, IReadOnlyCollection<int>? accessiblePipelineIds, CancellationToken ct);

    /// <summary>How many pipeline runs the organization can execute at once: its runners the scheduler may use,
    /// never below one.</summary>
    Task<int> GetExecutionCapacityAsync(int organizationId, CancellationToken ct);

    /// <summary>The latest run of each pipeline (by start), with the gate and grade of its latest analysis,
    /// in a single query. A pipeline that never ran is absent from the result.</summary>
    Task<Dictionary<int, ScheduleLastRunDto>> GetLastRunsAsync(IReadOnlyCollection<int> pipelineIds, CancellationToken ct = default);

    /// <summary>Average duration of a batch of pipelines in a single query: the mean of
    /// <c>CompletedAt - StartedAt</c> over the last ten successful runs of each pipeline, in minutes
    /// (rounded up, at least 1). A run that waited for an approval counts that wait in its duration.
    /// A pipeline with no completed successful run is absent from the result.</summary>
    Task<Dictionary<int, int>> GetAverageMinutesAsync(IReadOnlyCollection<int> pipelineIds, CancellationToken ct = default);
}
