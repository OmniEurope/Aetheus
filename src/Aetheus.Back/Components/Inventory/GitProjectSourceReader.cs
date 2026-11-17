// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Components.Inventory;

/// <summary>Serves the project inventory from the bare repositories on disk, which a mirror is too.</summary>
public sealed class GitProjectSourceReader(
    IGitLightRepository repositories,
    IGitLightCliService cli,
    IGitLightService git) : IProjectSourceReader
{
    private const string BlobMode = "100";

    public async Task<ProjectSourceTree?> ReadTreeAsync(int repositoryId, string revision, CancellationToken ct)
    {
        var diskPath = await DiskPathAsync(repositoryId, ct).ConfigureAwait(false);
        if (diskPath is null) return null;
        var commits = await cli.GetCommitsAsync(diskPath, revision, 0, 1, ct: ct).ConfigureAwait(false);
        if (commits.Count == 0) return null;
        var blobs = await cli.GetTreeBlobsAsync(diskPath, commits[0].Sha, ct).ConfigureAwait(false);
        if (blobs is null) return null;
        // "<mode> <blob id>": a submodule is mode 160000, another repository's commit and no file here.
        var files = blobs
            .Where(blob => blob.Value.StartsWith(BlobMode, StringComparison.Ordinal))
            .Select(blob => new ProjectSourceFile(blob.Key, blob.Value[(blob.Value.IndexOf(' ', StringComparison.Ordinal) + 1)..]))
            .ToList();
        return new ProjectSourceTree(commits[0].Sha, files);
    }

    public async Task<Stream?> OpenFileAsync(int repositoryId, string commitSha, string path, CancellationToken ct)
    {
        var diskPath = await DiskPathAsync(repositoryId, ct).ConfigureAwait(false);
        return diskPath is null ? null : await cli.GetBlobStreamAsync(diskPath, commitSha, path, ct).ConfigureAwait(false);
    }

    private async Task<string?> DiskPathAsync(int repositoryId, CancellationToken ct)
    {
        var repository = await repositories.FindByIdAsync(repositoryId, ct).ConfigureAwait(false);
        if (repository is null) return null;
        var diskPath = git.ResolveDiskPath(repository.ProjectId, repository.Slug);
        return Directory.Exists(diskPath) ? diskPath : null;
    }
}
