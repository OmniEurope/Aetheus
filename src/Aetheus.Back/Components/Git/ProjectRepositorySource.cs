// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

/// <summary>
/// Recette R1-32: the project's own source as the project records it. <see cref="RepositoryUrl"/> is
/// set for an internal repository, an attached external mirror and a plain external address alike;
/// <see cref="GitConnectionId"/> is set only when the source is an attached external repository.
/// </summary>
public sealed record ProjectRepositorySource(string? RepositoryUrl, int? GitConnectionId)
{
    /// <summary>Whether the project names a repository at all, internal or external.</summary>
    public bool HasSource => GitConnectionId is not null || !string.IsNullOrWhiteSpace(RepositoryUrl);
}
