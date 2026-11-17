// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.ExternalRepos;

/// <summary>
/// Recette R-4 (decision of 2026-11-10, option B): Aetheus reads an external repository only through a project mirror,
/// never straight from the address a project names. A project's repository address is one of Aetheus's own clone
/// URLs (<see cref="MirrorRepoPath"/>) once its mirror exists; any other address is an external repository nobody has
/// mirrored yet, which nothing reads until its mirror is created.
/// </summary>
public static class ExternalRepositoryAddress
{
    /// <summary>True when <paramref name="repositoryUrl"/> is set and is not one of Aetheus's own clone URLs.</summary>
    public static bool IsUnmirrored(string? repositoryUrl) =>
        !string.IsNullOrWhiteSpace(repositoryUrl)
        && !(Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) && MirrorRepoPath.IsMirrorPath(uri.AbsolutePath));
}
