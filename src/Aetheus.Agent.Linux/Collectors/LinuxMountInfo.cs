// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Linux.Collectors;

/// <summary>
/// The device behind each mount point, read from <c>/proc/self/mountinfo</c> (the mount table of the
/// agent's own mount namespace, so the paths a systemd unit makes writable appear there too). Two
/// mounts of one device, a bind mount or a Btrfs subvolume, report the very same capacity; the key lets
/// the collector count that storage once.
/// </summary>
internal static class LinuxMountInfo
{
    private const string MountInfoPath = "/proc/self/mountinfo";

    /// <summary>The key of the device mounted at <paramref name="mountPoint"/>, or null when the mount
    /// table is unreadable or does not list it.</summary>
    internal static string? DeviceKeyOf(string mountPoint)
    {
        try
        {
            return Parse(File.ReadAllText(MountInfoPath)).GetValueOrDefault(mountPoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Mount point to device key. A block device is keyed by its node (<c>/dev/sda1</c>), the
    /// same for every mount and subvolume of it; any other source (ZFS dataset, NFS export, FUSE) by
    /// its <c>major:minor</c> number, which is what makes a bind mount of it the same device. When a
    /// mount point is listed several times, the last mount is the visible one.</summary>
    internal static Dictionary<string, string> Parse(string mountInfo)
    {
        ArgumentNullException.ThrowIfNull(mountInfo);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw": the optional fields end at "-".
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var head = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tail = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (head.Length < 5 || tail.Length < 2) continue;

            var source = Unescape(tail[1]);
            keys[Unescape(head[4])] = source.StartsWith("/dev/", StringComparison.Ordinal) ? source : head[2];
        }
        return keys;
    }

    // The kernel writes space, tab, newline and backslash of a path as an octal escape (\040).
    private static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal)) return field;
        var text = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && IsOctal(field, i + 1))
            {
                text.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                text.Append(field[i]);
            }
        }
        return text.ToString();
    }

    private static bool IsOctal(string field, int start) =>
        start + 3 <= field.Length && field.AsSpan(start, 3).IndexOfAnyExceptInRange('0', '7') < 0;
}
