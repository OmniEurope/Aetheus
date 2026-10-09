// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Recette R2-122: deleting a project removes everything that belongs to it, so the project can be
/// created again from scratch (pipelines included, with the wizard).
/// </summary>
public interface IProjectDeletionService
{
    /// <summary>What a deletion would remove, counted per kind. Null when the project does not exist.</summary>
    Task<ProjectDeletionPreviewDto?> GetPreviewAsync(int projectId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the project and everything it owns in one transaction. False when it does not exist;
    /// <see cref="ConflictException"/> while one of its pipeline runs has not ended.
    /// </summary>
    Task<bool> DeleteAsync(int projectId, CancellationToken ct = default);
}
