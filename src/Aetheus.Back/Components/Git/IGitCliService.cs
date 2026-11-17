// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

/// <summary>Git commands run against a repository address. Recette R-4 (decision of 2026-11-10, option B): the direct
/// reads of an external address (branch resolution, pipeline file fetch) were removed; an external repository is read
/// only through its project mirror.</summary>
public interface IGitCliService
{
    Task<List<(string BranchName, string Version)>> ListReleaseBranchesAsync(string repositoryUrl, CancellationToken ct = default);
}
