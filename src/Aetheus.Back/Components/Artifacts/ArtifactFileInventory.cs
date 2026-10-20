// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Artifacts;

/// <summary>
/// Recette R1-73: the payload files the artifact store holds on disk, for the cleanup of the files no
/// artifact row references any more. Only the layout <see cref="ArtifactStorageService"/> writes is
/// listed, <c>{project}/{pipeline}/{run}/{file}</c> with numeric directories; the chunked-upload area
/// (<c>.uploads</c>) and the temporary <c>.*.upload</c> files of a transfer in progress are never listed.
/// </summary>
public sealed class ArtifactFileInventory(IConfiguration configuration)
{
    private readonly string _basePath = Path.GetFullPath(configuration.GetValue("ArtifactStorage:BasePath", "./data/artifacts")!);

    /// <summary>Paths of the payload files last written before <paramref name="writtenBeforeUtc"/>,
    /// relative to the store, in the form <see cref="ArtifactStorageService"/> records them.</summary>
    public List<string> ListFilesWrittenBefore(DateTime writtenBeforeUtc)
    {
        var files = new List<string>();
        if (!Directory.Exists(_basePath)) return files;
        foreach (var fullPath in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_basePath, fullPath);
            if (!IsPayloadLayout(relativePath)) continue;
            if (File.GetLastWriteTimeUtc(fullPath) >= writtenBeforeUtc) continue;
            files.Add(relativePath);
        }
        return files;
    }

    /// <summary>A comparable form of a recorded <c>FilePath</c>: the full path it designates in the store.
    /// Null for an empty value or one no path can hold, which designates no file of the store.</summary>
    public string? Key(string filePath) =>
        string.IsNullOrWhiteSpace(filePath) || filePath.Contains('\0')
            ? null
            : Path.GetFullPath(Path.Combine(_basePath, filePath));

    private static bool IsPayloadLayout(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Length == 4
            && segments.Take(3).All(segment => segment.Length > 0 && segment.All(char.IsAsciiDigit))
            && !segments[3].StartsWith('.');
    }
}
