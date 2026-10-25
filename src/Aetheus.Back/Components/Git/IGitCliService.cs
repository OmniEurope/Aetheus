// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

public interface IGitCliService
{
    Task<List<(string BranchName, string Version)>> ListReleaseBranchesAsync(string repositoryUrl, CancellationToken ct = default);

    Task<GitRemoteBranch> ResolveBranchCommitAsync(string repositoryUrl, string? branch, CancellationToken ct = default);

    Task<string?> ReadPipelineYamlAsync(string repositoryUrl, string commit, string pipelineName, CancellationToken ct = default);
}

public sealed record GitRemoteBranch(string Branch, string Commit);
