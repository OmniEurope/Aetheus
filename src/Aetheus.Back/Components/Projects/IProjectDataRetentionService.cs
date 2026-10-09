// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>PLAN-013 lot 4 (recette R2-115): a project's « Conservation des données » setting.</summary>
public interface IProjectDataRetentionService
{
    Task<ProjectDataRetentionDto?> GetAsync(int projectId, CancellationToken ct = default);
    Task<ProjectDataRetentionDto?> UpdateAsync(int projectId, UpdateProjectDataRetentionRequest request, CancellationToken ct = default);
}
