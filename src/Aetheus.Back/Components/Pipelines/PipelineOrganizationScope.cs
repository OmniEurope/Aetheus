// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// A pipeline has no organization column: it belongs to the organization of its project, or of the
/// project of its environment or project server. A pipeline with no owner resolves to 0, which no
/// organization has, so it is in no organization's scope.
/// </summary>
internal static class PipelineOrganizationScope
{
    public static IQueryable<Data.Entities.Pipeline> InOrganizations(
        this IQueryable<Data.Entities.Pipeline> pipelines,
        IReadOnlyCollection<int> organizationIds) =>
        pipelines.Where(pipeline => organizationIds.Contains(
            pipeline.Project != null
                ? pipeline.Project.OrganizationId
                : pipeline.Environment != null && pipeline.Environment.Project != null
                    ? pipeline.Environment.Project.OrganizationId
                    : pipeline.ProjectServer != null ? pipeline.ProjectServer.Project.OrganizationId : 0));
}
