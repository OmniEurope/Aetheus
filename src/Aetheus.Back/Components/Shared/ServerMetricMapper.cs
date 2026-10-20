// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// The stored metric row and its API contract carry the same figures; both directions of the copy
/// live here, side by side, so a column added to one is seen missing from the other.
/// </summary>
internal static class ServerMetricMapper
{
    public static ServerMetricDto ToDto(ServerMetric row) => new()
    {
        ServerId = row.ServerId,
        CpuPercent = row.CpuPercent,
        MemoryUsedMb = row.MemoryUsedMb,
        MemoryTotalMb = row.MemoryTotalMb,
        DiskUsedGb = row.DiskUsedGb,
        DiskTotalGb = row.DiskTotalGb,
        InodeUsedPercent = row.InodeUsedPercent,
        ClockOffsetMs = row.ClockOffsetMs,
        ClockOffsetUncertaintyMs = row.ClockOffsetUncertaintyMs,
        BuildCacheAvailable = row.BuildCacheAvailable,
        DockerInventoryAvailable = row.DockerInventoryAvailable,
        BuildCacheBytes = row.BuildCacheBytes,
        BuildCacheReclaimableBytes = row.BuildCacheReclaimableBytes,
        DockerImagesBytes = row.DockerImagesBytes,
        DockerContainersBytes = row.DockerContainersBytes,
        DockerVolumesBytes = row.DockerVolumesBytes,
        AgentWorkDirectoryBytes = row.AgentWorkDirectoryBytes,
        AgentInstallDirectoryBytes = row.AgentInstallDirectoryBytes,
        NuGetCacheBytes = row.NuGetCacheBytes,
        JournalBytes = row.JournalBytes,
        StorageMaintenanceDryRun = row.StorageMaintenanceDryRun,
        DeploymentOnly = row.DeploymentOnly,
        BuildActive = row.BuildActive,
        LastBuildAttemptAtUtc = row.LastBuildAttemptAtUtc,
        Timestamp = row.Timestamp
    };

    public static ServerMetric ToEntity(ServerMetricDto metric) => new()
    {
        ServerId = metric.ServerId,
        CpuPercent = metric.CpuPercent,
        MemoryUsedMb = metric.MemoryUsedMb,
        MemoryTotalMb = metric.MemoryTotalMb,
        DiskUsedGb = metric.DiskUsedGb,
        DiskTotalGb = metric.DiskTotalGb,
        InodeUsedPercent = metric.InodeUsedPercent,
        ClockOffsetMs = metric.ClockOffsetMs,
        ClockOffsetUncertaintyMs = metric.ClockOffsetUncertaintyMs,
        BuildCacheAvailable = metric.BuildCacheAvailable,
        DockerInventoryAvailable = metric.DockerInventoryAvailable,
        BuildCacheBytes = metric.BuildCacheBytes,
        BuildCacheReclaimableBytes = metric.BuildCacheReclaimableBytes,
        DockerImagesBytes = metric.DockerImagesBytes,
        DockerContainersBytes = metric.DockerContainersBytes,
        DockerVolumesBytes = metric.DockerVolumesBytes,
        AgentWorkDirectoryBytes = metric.AgentWorkDirectoryBytes,
        AgentInstallDirectoryBytes = metric.AgentInstallDirectoryBytes,
        NuGetCacheBytes = metric.NuGetCacheBytes,
        JournalBytes = metric.JournalBytes,
        StorageMaintenanceDryRun = metric.StorageMaintenanceDryRun,
        DeploymentOnly = metric.DeploymentOnly,
        BuildActive = metric.BuildActive,
        LastBuildAttemptAtUtc = metric.LastBuildAttemptAtUtc,
        Timestamp = metric.Timestamp
    };
}
