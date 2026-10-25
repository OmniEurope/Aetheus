// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Collectors;

/// <summary>One mounted filesystem as the collector read it: the disk to report (its
/// <see cref="DiskInfoDto.DeviceId"/> is the device behind it, null when the platform cannot tell, which
/// makes the entry its own device) and the filesystem type the operating system gave it.</summary>
internal readonly record struct DiskCandidate(DiskInfoDto Disk, string? FileSystem);

/// <summary>
/// Which mounted filesystems count as "the disks" of a server. A host lists many mounts that are not
/// capacity of their own: memory filesystems (tmpfs, devtmpfs), read-only images (squashfs, overlay)
/// and, above all, the same device mounted several times (a bind mount, a Btrfs subvolume, the paths a
/// systemd unit makes writable). Summing them counted one disk twice, so the total shown for a server
/// was a multiple of what it really holds.
/// </summary>
internal static class DiskSelector
{
    // Filesystems backed by memory, by a read-only image or by the kernel itself: none is storage the
    // operator can fill. Overlay is not listed: it is the root of a container, whose capacity is real.
    // Compared case-insensitively against DriveInfo.DriveFormat, which on Linux is the type field of
    // /proc/mounts.
    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmpfs", "devtmpfs", "ramfs", "squashfs", "proc", "sysfs", "cgroup", "cgroup2",
        "devpts", "mqueue", "debugfs", "tracefs", "securityfs", "pstore", "bpf", "configfs", "fusectl",
        "hugetlbfs", "autofs", "binfmt_misc", "nsfs", "efivarfs", "rpc_pipefs", "selinuxfs"
    };

    public static bool IsPseudoFileSystem(string? fileSystem) =>
        !string.IsNullOrWhiteSpace(fileSystem) && PseudoFileSystems.Contains(fileSystem);

    /// <summary>The disks to report: pseudo filesystems dropped, then one entry per device. Among the
    /// mounts of one device the shortest mount point is kept ("/" before "/home" or "/mnt/x"), so the
    /// report names the filesystem the operator knows. A candidate without a device key is kept as is.</summary>
    public static List<DiskInfoDto> Select(IEnumerable<DiskCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var disks = new List<DiskInfoDto>();
        foreach (var candidate in candidates
                     .Where(candidate => !IsPseudoFileSystem(candidate.FileSystem))
                     .OrderBy(candidate => candidate.Disk.MountPoint.Length))
        {
            if (candidate.Disk.DeviceId is { Length: > 0 } key && !seen.Add(key)) continue;
            disks.Add(candidate.Disk);
        }
        return disks;
    }
}
