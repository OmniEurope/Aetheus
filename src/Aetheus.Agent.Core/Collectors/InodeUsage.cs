// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Collectors;

/// <summary>Inodes of one filesystem: how many exist and how many are in use.</summary>
public readonly record struct InodeUsage(long Total, long Used)
{
    /// <summary>
    /// Reads the <c>f_files</c> and <c>f_ffree</c> fields of <c>statvfs</c>. A filesystem that reports
    /// no inodes at all (btrfs, some network and virtual filesystems allocate them on demand) has no
    /// table that can fill up, so it yields null rather than a reassuring 0 %; so does an incoherent
    /// reply where the free count exceeds the total.
    /// </summary>
    public static InodeUsage? FromStatvfs(ulong files, ulong freeFiles)
    {
        if (files == 0 || files > long.MaxValue || freeFiles > files) return null;
        return new InodeUsage((long)files, (long)(files - freeFiles));
    }
}
