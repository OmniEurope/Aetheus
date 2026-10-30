// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Shared;

public static class ProjectDtoMapper
{
    public static ProjectDto ToDto(Project project, bool includePipelineSummary = false)
    {
        var lastRun = includePipelineSummary
            ? project.Pipelines.SelectMany(pipeline => pipeline.Runs)
                .OrderByDescending(run => run.StartedAt)
                .FirstOrDefault()
            : null;
        return new ProjectDto
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            RepositoryUrl = project.RepositoryUrl,
            DefaultBranch = project.DefaultBranch,
            Status = project.Status,
            DeletionRequestedAt = project.DeletionRequestedAt,
            Tags = TagsHelper.DeserializeTags(project.Tags),
            PipelineCount = includePipelineSummary ? project.Pipelines.Count : 0,
            OrganizationId = project.OrganizationId,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            LastRunStatus = lastRun?.Status,
            LastRunAt = lastRun?.StartedAt
        };
    }
}
