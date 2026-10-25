// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The files of a <c>git archive --format=tar</c> read in memory, where the script extracted the archive to
/// a temporary folder with <c>tar -xf</c> and read the files back: the same bytes, without writing them.
/// A regular file is its content; a symbolic link is the target text Git stores for it (the script read the
/// file the extracted link pointed at, which depends on the host; the same text on both sides of a
/// comparison is the stable reading); directories and the archive's global header are not files.
/// </summary>
internal static class PublicDistributionArchive
{
    /// <summary>The paths of the files of the archive, without reading their content (<c>git ls-tree</c>).</summary>
    public static async Task<List<string>> ListAsync(string archivePath, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        await using var stream = File.OpenRead(archivePath);
        using var reader = new TarReader(stream);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (IsFile(entry))
                paths.Add(entry.Name);
        }
        return paths;
    }

    /// <summary>The content of the files of the archive, only those named in <paramref name="wanted"/> when
    /// it is given.</summary>
    public static async Task<Dictionary<string, byte[]>> ReadAsync(
        string archivePath, IReadOnlySet<string>? wanted, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var stream = File.OpenRead(archivePath);
        using var reader = new TarReader(stream);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (IsFile(entry) && (wanted is null || wanted.Contains(entry.Name)))
                files[entry.Name] = await ContentAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        return files;
    }

    private static bool IsFile(TarEntry entry) =>
        entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile or TarEntryType.SymbolicLink;

    private static async Task<byte[]> ContentAsync(TarEntry entry, CancellationToken cancellationToken)
    {
        if (entry.EntryType == TarEntryType.SymbolicLink)
            return Encoding.UTF8.GetBytes(entry.LinkName);
        if (entry.DataStream is null)
            return [];
        using var buffer = new MemoryStream();
        await entry.DataStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
