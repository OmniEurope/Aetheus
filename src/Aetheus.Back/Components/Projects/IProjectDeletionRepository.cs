// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>Reads and removes everything a project owns (see <see
/// cref="ProjectDeletionScope"/>).</summary>
public interface IProjectDeletionRepository
{
    /// <summary>Null when the project does not exist.</summary>
    Task<ProjectDeletionScope?> ResolveScopeAsync(int projectId, CancellationToken ct = default);

    Task<int> CountActiveRunsAsync(ProjectDeletionScope scope, CancellationToken ct = default);

    /// <summary>Every kind of owned data with its count, zero counts included.</summary>
    Task<Dictionary<ProjectDeletionItemKind, int>> CountAsync(ProjectDeletionScope scope, CancellationToken ct = default);

    /// <summary>Stored files of the project's artifacts, relative to the artifact storage.</summary>
    Task<List<string>> GetArtifactFilePathsAsync(ProjectDeletionScope scope, CancellationToken ct = default);

    /// <summary>Slugs of the project's internal Git repositories, whose bare repositories live on disk.</summary>
    Task<List<string>> GetGitRepositorySlugsAsync(ProjectDeletionScope scope, CancellationToken ct = default);

    /// <summary>
    /// Removes the project and everything in its scope, leaf to root, large tables in bounded
    /// batches. The caller holds the transaction: nothing here commits on its own.
    /// </summary>
    Task DeleteAsync(ProjectDeletionScope scope, CancellationToken ct = default);

    /// <summary>Recette R-51 (ADR-058): marks the project deleted, waiting for its server cleanup, at
    /// <paramref name="at"/> unless it already is. False when the project does not exist.</summary>
    Task<bool> MarkDeletionPendingAsync(int projectId, DateTime at, CancellationToken ct = default);
}
