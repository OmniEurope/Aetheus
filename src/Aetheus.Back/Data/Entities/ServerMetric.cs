// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class ServerMetric
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryUsedMb { get; set; }
    public double MemoryTotalMb { get; set; }
    public double DiskUsedGb { get; set; }
    public double DiskTotalGb { get; set; }
    /// <summary>Fullest inode table, in percent; null when the agent did not measure it.</summary>
    public double? InodeUsedPercent { get; set; }
    /// <summary>Agent clock offset against the backend's, in milliseconds (positive = agent ahead);
    /// null when the heartbeat carried no usable clock sample.</summary>
    public double? ClockOffsetMs { get; set; }
    /// <summary>Half the round trip of the measured exchange: the true offset lies within it.</summary>
    public double? ClockOffsetUncertaintyMs { get; set; }
    public bool BuildCacheAvailable { get; set; }
    public bool DockerInventoryAvailable { get; set; }
    public long BuildCacheBytes { get; set; }
    public long BuildCacheReclaimableBytes { get; set; }
    public long DockerImagesBytes { get; set; }
    public long DockerContainersBytes { get; set; }
    public long DockerVolumesBytes { get; set; }
    public long AgentWorkDirectoryBytes { get; set; }
    public long AgentInstallDirectoryBytes { get; set; }
    public long NuGetCacheBytes { get; set; }
    public long JournalBytes { get; set; }
    public bool StorageMaintenanceDryRun { get; set; }
    public bool DeploymentOnly { get; set; }
    public bool BuildActive { get; set; }
    public DateTime? LastBuildAttemptAtUtc { get; set; }
    public DateTime Timestamp { get; set; }

    // Navigation
    public Server Server { get; set; } = null!;
}
