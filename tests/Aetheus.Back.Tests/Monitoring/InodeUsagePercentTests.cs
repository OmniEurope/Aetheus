// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Tests.Monitoring;

/// <summary>
/// PLAN-010 lot 1: the stored metric carries the fullest inode table of the heartbeat, and a heartbeat
/// without inode counts stores "unknown", not 0 %.
/// </summary>
public sealed class InodeUsagePercentTests
{
    [Fact]
    public void MaxInodeUsedPercent_IsTheFullestFilesystem_NotTheSum()
    {
        DiskInfoDto[] disks =
        [
            new() { MountPoint = "/", InodesTotal = 1_000_000, InodesUsed = 100_000 },
            new() { MountPoint = "/home", InodesTotal = 1000, InodesUsed = 925 },
            new() { MountPoint = "/mnt/btrfs" }
        ];

        Assert.Equal(92.5, DiskInfoDto.MaxInodeUsedPercent(disks));
    }

    [Fact]
    public void MaxInodeUsedPercent_WithoutCounts_IsUnknown()
    {
        Assert.Null(DiskInfoDto.MaxInodeUsedPercent([new DiskInfoDto { MountPoint = "C:\\", UsedGb = 10, TotalGb = 100 }]));
        Assert.Null(DiskInfoDto.MaxInodeUsedPercent([]));
    }

    [Fact]
    public void Heartbeat_ToStoredMetric_KeepsTheInodePercent()
    {
        var heartbeat = new ServerHeartbeatDto
        {
            Disks = [new DiskInfoDto { MountPoint = "/", UsedGb = 10, TotalGb = 100, InodesTotal = 200, InodesUsed = 190 }]
        };

        var entity = ServerMetricMapper.ToEntity(ServerMetricDto.FromHeartbeat(heartbeat, 7, DateTime.UtcNow));

        Assert.Equal(95d, entity.InodeUsedPercent);
        Assert.Equal(95d, ServerMetricMapper.ToDto(entity).InodeUsedPercent);
    }

    [Fact]
    public void LegacyHeartbeat_StoresUnknownInodes()
    {
        var heartbeat = new ServerHeartbeatDto { Disks = [new DiskInfoDto { MountPoint = "/", UsedGb = 10, TotalGb = 100 }] };

        var entity = ServerMetricMapper.ToEntity(ServerMetricDto.FromHeartbeat(heartbeat, 7, DateTime.UtcNow));

        Assert.Null(entity.InodeUsedPercent);
    }
}
