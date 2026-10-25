// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Monitoring;

/// <summary>
/// R-47: the disk total of a server counts each storage once. A heartbeat that lists one device under
/// several mounts (an agent predating the device id included) must not double the total the overview
/// shows and the free-space alert reads.
/// </summary>
public sealed class DiskTotalsTests
{
    private static DiskInfoDto Disk(string mountPoint, double used, double total, string? deviceId = null, long? inodes = null) =>
        new() { MountPoint = mountPoint, UsedGb = used, TotalGb = total, DeviceId = deviceId, InodesTotal = inodes, InodesUsed = inodes is null ? null : 1000 };

    [Fact]
    public void TheSameDeviceMountedTwice_IsCountedOnce()
    {
        var disks = new[]
        {
            Disk("/", 198.4, 398.1, "/dev/sda1"),
            Disk("/mnt/data", 198.4, 398.1, "/dev/sda1")
        };

        Assert.Equal((198.4, 398.1), DiskInfoDto.Totals(disks));
        Assert.Equal("/", Assert.Single(DiskInfoDto.Distinct(disks)).MountPoint);
    }

    [Fact]
    public void AnAgentWithoutDeviceId_IsDeduplicatedBySizeInodesAndUse()
    {
        // The agent in production (1.5.5) lists the mounts without a device id.
        var disks = new[]
        {
            Disk("/", 198.4, 398.1, inodes: 26_214_400),
            Disk("/home/deploy", 198.4, 398.1, inodes: 26_214_400)
        };

        Assert.Equal((198.4, 398.1), DiskInfoDto.Totals(disks));
    }

    [Fact]
    public void TwoRealVolumes_AreBothCounted()
    {
        var disks = new[]
        {
            Disk("/", 50, 100, "/dev/sda1", 6_553_600),
            Disk("/mnt/volume", 300, 400, "/dev/sdb", 26_214_400)
        };

        Assert.Equal((350d, 500d), DiskInfoDto.Totals(disks));
    }

    [Fact]
    public void LegacyVolumesOfDifferentUse_AreNotMergedEvenWithTheSameSize()
    {
        var disks = new[]
        {
            Disk("/", 50, 100, inodes: 6_553_600),
            Disk("/mnt/volume", 12, 100, inodes: 6_553_600)
        };

        Assert.Equal((62d, 200d), DiskInfoDto.Totals(disks));
    }

    [Fact]
    public void ADiskWithADeviceId_IsNeverMergedWithOneWithout()
    {
        var disks = new[]
        {
            Disk("/", 50, 100, "/dev/sda1", 6_553_600),
            Disk("/other", 50, 100, inodes: 6_553_600)
        };

        Assert.Equal(200d, DiskInfoDto.Totals(disks).TotalGb);
    }

    [Fact]
    public void TheStoredMetric_ReadsTheDeduplicatedTotals()
    {
        var heartbeat = new ServerHeartbeatDto
        {
            Disks = [Disk("/", 198.4, 398.1, "/dev/sda1"), Disk("/mnt/data", 198.4, 398.1, "/dev/sda1")]
        };

        var metric = ServerMetricDto.FromHeartbeat(heartbeat, 5, DateTime.UtcNow);

        Assert.Equal(398.1, metric.DiskTotalGb);
        Assert.Equal(198.4, metric.DiskUsedGb);
    }

    [Fact]
    public void TheServerRow_KeepsTheDistinctDisks_AndReadsThemBack()
    {
        var server = new Server();
        var heartbeat = new ServerHeartbeatDto
        {
            Disks = [Disk("/", 198.4, 398.1, "/dev/sda1"), Disk("/mnt/data", 198.4, 398.1, "/dev/sda1"), Disk("/mnt/volume", 1, 10, "/dev/sdb")]
        };

        ServerDisks.Apply(server, heartbeat);
        var disks = ServerDisks.Read(server.DisksJson);

        Assert.Equal(["/", "/mnt/volume"], disks.Select(disk => disk.MountPoint));
        Assert.Equal("/dev/sdb", disks[1].DeviceId);
    }

    [Fact]
    public void AHeartbeatWithoutDisks_ClearsTheStoredList_AndABrokenColumnReadsEmpty()
    {
        var server = new Server { DisksJson = "[{\"MountPoint\":\"/\"}]" };

        ServerDisks.Apply(server, new ServerHeartbeatDto());

        Assert.Null(server.DisksJson);
        Assert.Empty(ServerDisks.Read(server.DisksJson));
        Assert.Empty(ServerDisks.Read("{not json"));
    }
}
