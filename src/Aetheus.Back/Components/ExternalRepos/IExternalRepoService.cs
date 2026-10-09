// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.ExternalRepos;

/// <summary>
/// Registration surface for mirror-backed external repositories (always on since recette R-295).
/// Enforces at most one external repository per project (the project's source, or an additional
/// source beside its internal repository, recette R-534) and keeps every credential on the backend.
/// </summary>
public interface IExternalRepoService
{
    Task<ExternalRepoDto?> GetForProjectAsync(int projectId, CancellationToken ct = default);

    /// <summary>
    /// Attaches an external repo to a project: persists the credential, the connection and the mirror
    /// pointer, runs a connectivity probe, performs the initial mirror clone, and marks the project's
    /// source as external. Throws <c>ConflictException</c> if the project already has a source.
    /// </summary>
    Task<ExternalRepoDto> AttachAsync(AttachExternalRepoRequest request, CancellationToken ct = default);

    /// <summary>Triggers an immediate mirror refresh for the project's external source.</summary>
    Task<ExternalRepoDto?> SyncNowAsync(int projectId, CancellationToken ct = default);

    /// <summary>Detaches the external source (removes connection + mirror + credential, reverts the project).</summary>
    Task<bool> DetachAsync(int projectId, CancellationToken ct = default);
}
