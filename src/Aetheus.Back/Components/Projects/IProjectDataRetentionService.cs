// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>Exposes the project data retention setting.</summary>
public interface IProjectDataRetentionService
{
    Task<ProjectDataRetentionDto?> GetAsync(int projectId, CancellationToken ct = default);
    Task<ProjectDataRetentionDto?> UpdateAsync(int projectId, UpdateProjectDataRetentionRequest request, CancellationToken ct = default);
}
