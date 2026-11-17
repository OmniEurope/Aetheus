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

    /// <summary>
    /// Recette R-4: creates the project mirror of the public HTTPS address the project names (an anonymous read), through
    /// <see cref="AttachAsync"/>, so the project's pipelines, workspace and releases read the mirror, never the address.
    /// Throws <c>ConflictException</c> when the project has a repository or a mirror already, <c>BadRequestException</c>
    /// when the address is not a public HTTPS repository address or cannot be reached.
    /// </summary>
    Task<ExternalRepoDto> MirrorRepositoryAddressAsync(int projectId, CancellationToken ct = default);

    /// <summary>Triggers an immediate mirror refresh for the project's external source.</summary>
    Task<ExternalRepoDto?> SyncNowAsync(int projectId, CancellationToken ct = default);

    /// <summary>Detaches the external source (removes connection + mirror + credential, reverts the project).</summary>
    Task<bool> DetachAsync(int projectId, CancellationToken ct = default);
}
