// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary> Deleting a project removes everything that belongs to it, so the project can be
/// created again from scratch (pipelines included, with the wizard), and what it deployed on its
/// servers (recette R-97, ADR-058). </summary>
public interface IProjectDeletionService
{
    /// <summary>What a deletion would remove, counted per kind, with the servers the project deployed to.
    /// Null when the project does not exist.</summary>
    Task<ProjectDeletionPreviewDto?> GetPreviewAsync(int projectId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the project and everything it owns in one transaction once the server tasks given
    /// (<paramref name="serverTaskIds"/>: the deletion's discoveries and removals) prove every server resource of the
    /// project removed: <see cref="ProjectDeletionOutcome.Deleted"/>. Otherwise (recette R-51) nothing is half
    /// deleted: the project is marked deleted, waiting for its server cleanup, and stays listed, unusable, until a
    /// relaunched cleanup proves everything removed: <see cref="ProjectDeletionOutcome.PendingCleanup"/>. Null when
    /// it does not exist; <see cref="ConflictException"/> while one of its pipeline runs has not ended.
    /// </summary>
    Task<ProjectDeletionOutcome?> DeleteAsync(int projectId, IReadOnlyCollection<int> serverTaskIds, CancellationToken ct = default);
}
