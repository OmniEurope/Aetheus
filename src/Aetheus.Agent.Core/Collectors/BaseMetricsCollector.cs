// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Collectors;

public abstract class BaseMetricsCollector : IMetricsCollector
{
    public ServerHeartbeatDto Collect()
    {
        return new ServerHeartbeatDto
        {
            CpuPercent = GetCpuPercent(),
            MemoryUsedMb = GetMemoryUsedMb(),
            MemoryTotalMb = GetMemoryTotalMb(),
            Disks = GetDisks(),
            Services = [],
            OomKills = GetOomKills()
        };
    }

    protected abstract double GetCpuPercent();
    protected abstract double GetMemoryUsedMb();
    protected abstract double GetMemoryTotalMb();

    /// <summary>
    /// Out-of-memory kills since boot, or null when the platform has no such counter (the default,
    /// Windows): the backend then stays silent rather than reading "no kill".
    /// </summary>
    protected virtual OomKillReportDto? GetOomKills() => null;

    protected List<DiskInfoDto> GetDisks()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        var candidates = new List<DiskCandidate>();
        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady || !ShouldIncludeDrive(drive))
                    continue;

                var totalGb = Math.Round(drive.TotalSize / 1073741824.0, 1);
                if (totalGb <= 0)
                    continue;

                var inodes = GetInodeUsage(drive.Name);
                var deviceKey = GetDeviceKey(drive.Name);
                candidates.Add(new DiskCandidate(
                    new DiskInfoDto
                    {
                        MountPoint = drive.Name,
                        UsedGb = Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / 1073741824.0, 1),
                        TotalGb = totalGb,
                        InodesTotal = inodes?.Total,
                        InodesUsed = inodes?.Used,
                        DeviceId = deviceKey
                    },
                    drive.DriveFormat));
            }
            catch (IOException) { /* drive disconnected/locked - skip */ }
            catch (UnauthorizedAccessException) { /* permission denied - skip */ }
        }
        // One entry per device and no pseudo filesystem: the sum of the report is then the real capacity.
        return DiskSelector.Select(candidates);
    }

    protected virtual bool ShouldIncludeDrive(DriveInfo drive) => true;

    /// <summary>
    /// Identity of the device behind the filesystem mounted at <paramref name="mountPoint"/>: two
    /// mounts with the same key are the same storage seen twice (bind mount, subvolume) and count once.
    /// Null when the platform cannot tell (the default): the mount then counts on its own.
    /// </summary>
    protected virtual string? GetDeviceKey(string mountPoint) => null;

    /// <summary>
    /// Inode counts of the filesystem mounted at <paramref name="mountPoint"/>, or null when the
    /// platform has no inode table to read (the default): the backend then shows it as unknown.
    /// </summary>
    protected virtual InodeUsage? GetInodeUsage(string mountPoint) => null;
}
