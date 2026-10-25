// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Linux.Collectors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-47: the disks of a server are its distinct storage. One device mounted several times (a bind
/// mount, a Btrfs subvolume, the paths a systemd unit makes writable) counts once, and memory or kernel
/// filesystems do not count at all; the sum of the report is then the real capacity.</summary>
public sealed class DiskSelectorTests
{
    private static DiskCandidate Mount(string mountPoint, double usedGb, double totalGb, string? fileSystem = "ext4", string? deviceId = null) =>
        new(new DiskInfoDto { MountPoint = mountPoint, UsedGb = usedGb, TotalGb = totalGb, DeviceId = deviceId }, fileSystem);

    [Fact]
    public void ADeviceMountedThreeTimes_IsCountedOnce_UnderItsShortestMountPoint()
    {
        // The production symptom: 398 Go of one disk read as 796 Go once "/" and a second mount of the
        // same device were summed.
        var disks = DiskSelector.Select(
        [
            Mount("/mnt/data", 198.4, 398.1, deviceId: "/dev/sda1"),
            Mount("/", 198.4, 398.1, deviceId: "/dev/sda1"),
            Mount("/home/deploy", 198.4, 398.1, deviceId: "/dev/sda1")
        ]);

        var only = Assert.Single(disks);
        Assert.Equal("/", only.MountPoint);
        Assert.Equal(398.1, DiskInfoDto.Totals(disks).TotalGb);
        Assert.Equal(198.4, DiskInfoDto.Totals(disks).UsedGb);
    }

    [Fact]
    public void TwoDifferentDevices_AreBothKept_AndSummed()
    {
        var disks = DiskSelector.Select(
        [
            Mount("/", 50, 100, deviceId: "/dev/sda1"),
            Mount("/mnt/volume", 300, 400, deviceId: "/dev/sdb")
        ]);

        Assert.Equal(2, disks.Count);
        Assert.Equal((350d, 500d), DiskInfoDto.Totals(disks));
    }

    [Theory]
    [InlineData("tmpfs")]
    [InlineData("devtmpfs")]
    [InlineData("squashfs")]
    [InlineData("proc")]
    [InlineData("cgroup2")]
    [InlineData("TMPFS")]
    public void PseudoFileSystems_AreNotDisks(string fileSystem)
    {
        var disks = DiskSelector.Select(
        [
            Mount("/", 50, 100, deviceId: "/dev/sda1"),
            Mount("/dev/shm", 0, 8, fileSystem)
        ]);

        Assert.Equal("/", Assert.Single(disks).MountPoint);
    }

    [Fact]
    public void AnOverlayRoot_IsKept_BecauseItIsTheCapacityOfAContainer() =>
        Assert.Single(DiskSelector.Select([Mount("/", 10, 50, "overlay", "overlay-root")]));

    [Fact]
    public void MountsWithoutDeviceKey_AreEachKept()
    {
        // A platform that cannot tell the device (Windows) keeps every drive, as before.
        var disks = DiskSelector.Select([Mount("C:\\", 10, 100, "NTFS"), Mount("D:\\", 10, 100, "NTFS")]);

        Assert.Equal(2, disks.Count);
    }

    [Fact]
    public void TheMountTable_OfABindMountAndASubvolume_SharesTheDeviceKey()
    {
        const string mountInfo =
            "26 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
            "40 26 8:1 /srv /mnt/data rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
            "41 26 0:35 /@home /home rw,relatime shared:2 - btrfs /dev/sda1 rw,subvol=/@home\n" +
            "50 26 0:44 / /dev/shm rw,nosuid,nodev shared:3 - tmpfs tmpfs rw\n" +
            "60 26 0:50 / /mnt/with\\040space rw,relatime - zfs pool/data rw\n";

        var keys = LinuxMountInfo.Parse(mountInfo);

        Assert.Equal("/dev/sda1", keys["/"]);
        Assert.Equal("/dev/sda1", keys["/mnt/data"]);
        Assert.Equal("/dev/sda1", keys["/home"]);
        Assert.Equal("0:44", keys["/dev/shm"]);
        Assert.Equal("0:50", keys["/mnt/with space"]);
    }

    [Fact]
    public void ReadingTheRealMountTable_NamesTheRootDevice_OnLinuxOnly()
    {
        var key = LinuxMountInfo.DeviceKeyOf("/");

        if (OperatingSystem.IsLinux())
            Assert.False(string.IsNullOrWhiteSpace(key), "The root filesystem must have a device key.");
        else
            Assert.Null(key);
    }
}
