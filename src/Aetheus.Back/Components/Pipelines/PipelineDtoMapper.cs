// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal static class PipelineDtoMapper
{
    internal static PipelineDto Map(Pipeline pipeline)
    {
        var lastRun = pipeline.Runs.FirstOrDefault();
        return new PipelineDto
        {
            Id = pipeline.Id,
            Name = pipeline.Name,
            Description = pipeline.Description,
            YamlDefinition = pipeline.YamlDefinition,
            TriggerType = pipeline.TriggerType,
            ProjectId = pipeline.ProjectId,
            SourceRepositoryId = pipeline.SourceRepositoryId,
            ProjectName = pipeline.Project?.Name,
            SourceBranch = pipeline.SourceBranch,
            EnvironmentId = pipeline.EnvironmentId,
            EnvironmentName = pipeline.Environment?.Name,
            ProjectServerId = pipeline.ProjectServerId,
            ProjectServerName = pipeline.ProjectServer?.DisplayName,
            AiRunnerProfileId = pipeline.AiRunnerProfileId,
            LastRunStatus = lastRun?.Status,
            LastRunAt = lastRun?.StartedAt,
            RecentRuns = pipeline.Runs.Select(run =>
            {
                var firstStep = run.StepRuns.FirstOrDefault(step => !step.IsSystem && step.ServerId != null);
                return new PipelineRunSummaryDto
                {
                    Id = run.Id,
                    Status = run.Status,
                    StartedAt = run.StartedAt,
                    CompletedAt = run.CompletedAt,
                    ServerName = firstStep?.Server?.Name,
                    ServerOs = firstStep?.Server?.OsDescription
                };
            }).ToList(),
            CreatedAt = pipeline.CreatedAt,
            UpdatedAt = pipeline.UpdatedAt,
            OwnerUsername = pipeline.CreatedByUsername
        };
    }
}
