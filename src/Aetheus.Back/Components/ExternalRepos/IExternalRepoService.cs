// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.ExternalRepos;

/// <summary>Registers at most one mirror-backed external repository per project, either as its
/// source or beside its internal repository. Credentials remain on the backend.</summary>
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
