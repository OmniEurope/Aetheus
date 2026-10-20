// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Who owns a Git-synchronized pipeline when no authenticated pusher does: the Owner of the project's
/// organization, and whether a username still maps to an active user. The port of the pipeline ownership
/// resolver (audit AUD-07). <see cref="IPipelineRepository"/> composes it and delegates to the same
/// queries.
/// </summary>
public interface IPipelineProjectOwnerReader
{
    // F-INF-02: username of the project's owning-organization Owner, or null when unresolvable.
    Task<string?> GetProjectOwnerUsernameAsync(int projectId, CancellationToken ct = default);
    // Checks whether the pipeline owner maps to an active user.
    Task<bool> IsActiveUsernameAsync(string username, CancellationToken ct = default);
}
