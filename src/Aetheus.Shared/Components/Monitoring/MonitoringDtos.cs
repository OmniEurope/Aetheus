// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Monitoring;

public sealed record ServiceActionRequest
{
    [Required]
    public ServiceAction Action { get; init; }

    [Required]
    [StringLength(200, MinimumLength = 1)]
    [RegularExpression(@"^[a-zA-Z0-9][a-zA-Z0-9_.\-@]{0,63}$",
        ErrorMessage = "Service name contains invalid characters.")]
    public string ServiceName { get; init; } = string.Empty;
}

public sealed record ServiceInstallRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [RegularExpression(@"^[a-zA-Z0-9][a-zA-Z0-9_.\-@]{0,63}$",
        ErrorMessage = "Service name contains invalid characters.")]
    public string ServiceName { get; init; } = string.Empty;
}

public sealed record ServiceLogsRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string ServiceName { get; init; } = string.Empty;

    [Range(10, 1000)]
    public int Lines { get; init; } = 100;

    public bool Follow { get; init; }
}

public sealed record ServiceLogsResponse
{
    public int TaskId { get; init; }
}

/// <summary>
/// Returned by the service action / install / uninstall endpoints so the caller can correlate the
/// resulting <c>TaskCompleted</c> notification by id rather than by task-name suffix (which collides
/// when two operations target the same service).
/// </summary>
public sealed record ServiceTaskResponse
{
    public int TaskId { get; init; }
}

public sealed record ServerMetricDto : StorageUsageDto
{
    public int ServerId { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryUsedMb { get; init; }
    public double MemoryTotalMb { get; init; }
    public double DiskUsedGb { get; init; }
    public double DiskTotalGb { get; init; }
    /// <summary>Fullest inode table, in percent; null when the agent did not measure it.</summary>
    public double? InodeUsedPercent { get; init; }
    /// <summary>Agent clock offset against the backend's, in milliseconds (positive = agent ahead),
    /// and its uncertainty; both null when the heartbeat carried no usable clock sample.</summary>
    public double? ClockOffsetMs { get; init; }
    public double? ClockOffsetUncertaintyMs { get; init; }
    public bool StorageMaintenanceDryRun { get; init; }
    public bool DeploymentOnly { get; init; }
    public bool BuildActive { get; init; }
    public DateTime? LastBuildAttemptAtUtc { get; init; }
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// The metric point one heartbeat stands for. The API stores it and the server page draws it live
    /// from the same heartbeat: both read it here, so a figure added to the heartbeat reaches the
    /// stored history and the live chart together.
    /// </summary>
    public static ServerMetricDto FromHeartbeat(ServerHeartbeatDto heartbeat, int serverId, DateTime timestamp)
    {
        var storage = heartbeat.StorageDiagnostics;
        var clock = heartbeat.ClockSample?.Estimate();
        return new ServerMetricDto
        {
            ServerId = serverId,
            CpuPercent = heartbeat.CpuPercent,
            MemoryUsedMb = heartbeat.MemoryUsedMb,
            MemoryTotalMb = heartbeat.MemoryTotalMb,
            DiskUsedGb = DiskInfoDto.Totals(heartbeat.Disks).UsedGb,
            DiskTotalGb = DiskInfoDto.Totals(heartbeat.Disks).TotalGb,
            InodeUsedPercent = DiskInfoDto.MaxInodeUsedPercent(heartbeat.Disks),
            ClockOffsetMs = clock?.OffsetMs,
            ClockOffsetUncertaintyMs = clock?.UncertaintyMs,
            BuildCacheAvailable = storage.BuildCacheAvailable,
            DockerInventoryAvailable = storage.DockerInventoryAvailable,
            BuildCacheBytes = storage.BuildCacheBytes,
            BuildCacheReclaimableBytes = storage.BuildCacheReclaimableBytes,
            DockerImagesBytes = storage.DockerImagesBytes,
            DockerContainersBytes = storage.DockerContainersBytes,
            DockerVolumesBytes = storage.DockerVolumesBytes,
            AgentWorkDirectoryBytes = storage.AgentWorkDirectoryBytes,
            AgentInstallDirectoryBytes = storage.AgentInstallDirectoryBytes,
            NuGetCacheBytes = storage.NuGetCacheBytes,
            JournalBytes = storage.JournalBytes,
            StorageMaintenanceDryRun = storage.DryRun,
            DeploymentOnly = storage.DeploymentOnly,
            BuildActive = storage.BuildActive,
            LastBuildAttemptAtUtc = storage.LastBuildAttemptAtUtc,
            Timestamp = timestamp
        };
    }
}

public sealed record AlertRuleDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int? ServerId { get; init; }
    public string? ServerName { get; init; }
    public MetricType Metric { get; init; }
    public ComparisonOperator Operator { get; init; }
    public double Threshold { get; init; }
    public int SustainedSeconds { get; init; }
    public AlertSeverity Severity { get; init; }
    public bool IsEnabled { get; init; }
    public int? NotificationChannelId { get; init; }
    public string? NotificationChannelName { get; init; }
    public DateTime? LastTriggeredAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

public abstract record AlertRuleRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty; // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)

    public int? ServerId { get; set; } // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)

    [Required]
    public MetricType Metric { get; set; } // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)

    [Required]
    public ComparisonOperator Operator { get; set; } // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)

    [Required]
    [Range(0, double.MaxValue)]
    public double Threshold { get; set; } // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)

    [Range(10, 3600)]
    public int SustainedSeconds { get; set; } = 60; // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)

    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning; // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)

    public int? NotificationChannelId { get; set; } // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)

    public bool IsEnabled { get; set; } = true; // audit: kept set; (mutated post-construction by Components/Alerts/Alerts.razor @bind)
}

public sealed record CreateAlertRuleRequest : AlertRuleRequest;

public sealed record UpdateAlertRuleRequest : AlertRuleRequest;

public sealed record EnvironmentCheckDto
{
    public int Id { get; init; }
    public int EnvironmentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public EnvironmentCheckType Type { get; init; }
    public string? Configuration { get; init; }
    public bool IsRequired { get; init; }
    public int TimeoutSeconds { get; init; }
    public DateTime CreatedAt { get; init; }
}
