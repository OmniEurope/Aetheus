// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;

namespace Aetheus.Back.Components.Artifacts;

public sealed class ArtifactStorageService(IConfiguration configuration) : IArtifactStorageService
{
    private readonly string _basePath = configuration.GetValue("ArtifactStorage:BasePath", "./data/artifacts")!;

    private string SafeResolvePath(string filePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_basePath, filePath));
        // Compare against the base path WITH a trailing separator so a sibling prefix
        // (e.g. "/data/artifacts-evil" vs base "/data/artifacts") cannot satisfy the guard.
        var basePrefix = Path.GetFullPath(_basePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path traversal detected");
        return fullPath;
    }

    public async Task<(string RelativePath, string Sha256)> SaveArtifactAsync(int projectId, int pipelineId, int runId, string fileName, Stream content, CancellationToken ct = default)
    {
        var (relativeDirectory, safeName, fullPath) = PrepareDestination(projectId, pipelineId, runId, fileName);

        // First stream into a unique temporary file and hash the exact bytes. The final path includes
        // both that digest and a publication id, and FileMode.CreateNew makes the move non-overwriting.
        // A retry of the same logical artifact therefore creates a new immutable object instead of
        // changing bytes referenced by an existing database row.
        var temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Guid.NewGuid():N}.upload");
        try
        {
            using var sha = SHA256.Create();
            await using (var fileStream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var cryptoStream = new CryptoStream(fileStream, sha, CryptoStreamMode.Write, leaveOpen: true))
            {
                await content.CopyToAsync(cryptoStream, ct).ConfigureAwait(false);
                await cryptoStream.FlushFinalBlockAsync(ct).ConfigureAwait(false);
            }

            var digest = Convert.ToHexStringLower(sha.Hash!);
            var relativePath = ImmutablePath(relativeDirectory, safeName, digest);
            File.Move(temporaryPath, SafeResolvePath(relativePath), overwrite: false);
            return (relativePath, digest);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task<string> AdoptVerifiedArtifactAsync(int projectId, int pipelineId, int runId, string fileName, VerifiedArtifactFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        // Only a file this store keeps beside its artifacts (the chunked uploads directory) can be
        // adopted, never an arbitrary path; the same traversal guard as every other path here.
        var source = SafeResolvePath(Path.GetRelativePath(_basePath, file.Path));
        var (relativeDirectory, safeName, _) = PrepareDestination(projectId, pipelineId, runId, fileName);
        var relativePath = ImmutablePath(relativeDirectory, safeName, file.Sha256.ToLowerInvariant());
        // A rename on the same volume: the bytes were hashed while being assembled, so nothing is
        // read or written again (recette R1-91). Never overwrites, like the streamed path.
        File.Move(source, SafeResolvePath(relativePath), overwrite: false);
        return Task.FromResult(relativePath);
    }

    /// <summary>The run directory, created, and the validated file name of a new artifact.</summary>
    private (string RelativeDirectory, string SafeName, string FullPath) PrepareDestination(
        int projectId, int pipelineId, int runId, string fileName)
    {
        var normalized = fileName.Replace('\\', '/');
        var safeName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(safeName) || safeName != normalized)
            throw new BadRequestException("Invalid artifact file name.");
        var relativeDirectory = Path.Combine(projectId.ToString(), pipelineId.ToString(), runId.ToString());
        // Route through SafeResolvePath like the read/delete paths (closes the audit-noted asymmetry).
        // Safe already (int ids + normalized name), but the traversal guard is now applied uniformly.
        var fullPath = SafeResolvePath(Path.Combine(relativeDirectory, safeName));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return (relativeDirectory, safeName, fullPath);
    }

    /// <summary>The final name holds the digest and a publication id, so a retry of the same logical
    /// artifact creates a new immutable object instead of changing bytes an existing row references.</summary>
    private static string ImmutablePath(string relativeDirectory, string safeName, string digest) =>
        Path.Combine(relativeDirectory,
            $"{Path.GetFileNameWithoutExtension(safeName)}.{digest}.{Guid.NewGuid():N}{Path.GetExtension(safeName)}");

    public Task DeleteArtifactAsync(string filePath, CancellationToken ct = default)
    {
        var fullPath = SafeResolvePath(filePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);

            var dir = Path.GetDirectoryName(fullPath)!;
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, recursive: true);
        }
        return Task.CompletedTask;
    }

    public Stream? OpenArtifact(string filePath)
    {
        var fullPath = SafeResolvePath(filePath);
        return File.Exists(fullPath) ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
    }

    public long GetArtifactSize(string filePath)
    {
        var fullPath = SafeResolvePath(filePath);
        return File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
    }
}
