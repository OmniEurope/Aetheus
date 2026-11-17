// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Inventory;

public sealed record ProjectSourceFile(string Path, string BlobId);

public sealed record ProjectSourceTree(string CommitSha, IReadOnlyList<ProjectSourceFile> Files);

/// <summary>
/// Reads the files of a repository, internal or mirrored: the seam that lets the coverage decision run
/// over an in-memory tree.
/// </summary>
public interface IProjectSourceReader
{
    /// <summary>Every file of a revision. Null when the revision or its full listing cannot be read.</summary>
    Task<ProjectSourceTree?> ReadTreeAsync(int repositoryId, string revision, CancellationToken ct);

    Task<Stream?> OpenFileAsync(int repositoryId, string commitSha, string path, CancellationToken ct);
}
