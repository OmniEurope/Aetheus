// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public static class ProjectBreadcrumbExtensions
{
    public static void SetProjectResource(
        this BreadcrumbService breadcrumb,
        int? projectId,
        string? detailProjectName,
        IEnumerable<ProjectDto> projects,
        string projectsLabel,
        string projectLabel,
        string resourceLabel,
        string projectSegment,
        string rootHref,
        BreadcrumbItem current)
    {
        if (projectId is not { } id)
        {
            breadcrumb.Set(new BreadcrumbItem(resourceLabel, rootHref), current);
            return;
        }

        var projectName = detailProjectName
            ?? projects.FirstOrDefault(project => project.Id == id)?.Name
            ?? $"{projectLabel} #{id}";
        breadcrumb.Set(
            new BreadcrumbItem(projectsLabel, "/projects"),
            new BreadcrumbItem(projectName, $"/projects/{id}/overview"),
            new BreadcrumbItem(resourceLabel, $"/projects/{id}/{projectSegment}"),
            current);
    }
}
