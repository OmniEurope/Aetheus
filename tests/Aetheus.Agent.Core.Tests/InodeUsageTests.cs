// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Linux.Collectors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>PLAN-010 lot 1: inode counts reach the heartbeat, and an unreadable table stays unknown.</summary>
public sealed class InodeUsageTests
{
    [Fact]
    public void FromStatvfs_UsedIsTotalMinusFree()
    {
        Assert.Equal(new InodeUsage(6_553_600, 6_000_000), InodeUsage.FromStatvfs(6_553_600, 553_600));
    }

    [Theory]
    [InlineData(0UL, 0UL)]          // btrfs and friends: no fixed table
    [InlineData(100UL, 101UL)]      // incoherent reply
    [InlineData(ulong.MaxValue, 0UL)] // does not fit the contract
    public void FromStatvfs_WithoutAMeaningfulTable_IsUnknown(ulong files, ulong freeFiles)
    {
        Assert.Null(InodeUsage.FromStatvfs(files, freeFiles));
    }

    [Fact]
    public void Collect_CarriesTheInodeCountsOfEveryDisk()
    {
        var collector = new InodeReportingCollector(new InodeUsage(1000, 950));

        var disks = collector.Collect().Disks;

        Assert.NotEmpty(disks);
        Assert.All(disks, disk =>
        {
            Assert.Equal(1000, disk.InodesTotal);
            Assert.Equal(950, disk.InodesUsed);
        });
        Assert.Equal(95d, DiskInfoDto.MaxInodeUsedPercent(disks));
    }

    [Fact]
    public void Collect_WithoutInodeSupport_LeavesTheCountsUnknown()
    {
        var disks = new InodeReportingCollector(null).Collect().Disks;

        Assert.NotEmpty(disks);
        Assert.All(disks, disk => Assert.Null(disk.InodesTotal));
        Assert.Null(DiskInfoDto.MaxInodeUsedPercent(disks));
    }

    [Fact]
    public void LinuxStatvfs_ReadsTheRootFilesystemOnLinuxOnly()
    {
        var root = LinuxStatvfs.Read("/");

        if (OperatingSystem.IsLinux())
        {
            // A real statvfs call on the test hosts' root (ext4/overlayfs): a table with sane counts.
            // A null here means the P/Invoke or the struct layout is broken, not "unknown".
            Assert.NotNull(root);
            var usage = root.Value;
            Assert.True(usage.Total > 0, $"InodesTotal must be positive, got {usage.Total}.");
            Assert.InRange(usage.Used, 0, usage.Total);
        }
        else
        {
            Assert.Null(root);
        }
    }

    private sealed class InodeReportingCollector(InodeUsage? usage) : BaseMetricsCollector
    {
        protected override double GetCpuPercent() => 0;
        protected override double GetMemoryUsedMb() => 0;
        protected override double GetMemoryTotalMb() => 0;
        protected override InodeUsage? GetInodeUsage(string mountPoint) => usage;
    }
}
