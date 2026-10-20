// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Docker;

// --- Containers ---

public sealed record DockerContainerDto
{
    [StringLength(64)]
    public string ContainerId { get; init; } = string.Empty;
    [StringLength(500)]
    public string Name { get; init; } = string.Empty;
    [StringLength(500)]
    public string Image { get; init; } = string.Empty;
    [StringLength(64)]
    public string State { get; init; } = string.Empty;
    [StringLength(500)]
    public string Status { get; init; } = string.Empty;
    [StringLength(2000)]
    public string Ports { get; init; } = string.Empty;
    public DateTime Created { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryUsageMb { get; init; }
    public double MemoryLimitMb { get; init; }
    [StringLength(500)]
    public string Project { get; init; } = string.Empty;
    /// <summary>False when the agent did not read the container's settings (older agent, inspect
    /// failed): <see cref="Risks"/> is then unknown, not clean.</summary>
    public bool SecurityInspected { get; init; }
    public DockerContainerRisks Risks { get; init; }
    /// <summary>Comma-separated added capabilities among <see cref="DockerContainerSecurity.DangerousCapabilityNames"/>.</summary>
    [StringLength(500)]
    public string DangerousCapabilities { get; init; } = string.Empty;
    public AetheusContainerRole AetheusRole { get; init; }
}

public sealed record DockerImageDto
{
    [StringLength(128)]
    public string ImageId { get; init; } = string.Empty;
    [StringLength(500)]
    public string Repository { get; init; } = string.Empty;
    [StringLength(200)]
    public string Tag { get; init; } = string.Empty;
    [StringLength(50)]
    public string Size { get; init; } = string.Empty;
    public DateTime Created { get; init; }
    [StringLength(500)]
    public string Project { get; init; } = string.Empty;
}

public sealed record DockerActionRequest
{
    [Required]
    [StringLength(64)]
    public string ContainerId { get; init; } = string.Empty;

    public DockerContainerAction Action { get; init; }
}

public sealed record DockerContainerLogsRequest
{
    [Required]
    [StringLength(64)]
    public string ContainerId { get; init; } = string.Empty;

    [Range(1, 10000)]
    public int Tail { get; init; } = 100;
}

// --- Images ---

public sealed record DockerPullImageRequest
{
    [Required]
    [StringLength(200)]
    public string Image { get; init; } = string.Empty;
}

// --- Compose ---

public sealed record DockerComposeStackDto
{
    [StringLength(200)]
    public string Name { get; init; } = string.Empty;
    [StringLength(100)]
    public string Status { get; init; } = string.Empty;
    [StringLength(500)]
    public string ConfigFile { get; init; } = string.Empty;
    public int RunningCount { get; init; }
    public int TotalCount { get; init; }
}

public sealed record DockerComposeActionRequest
{
    [Required]
    [StringLength(200)]
    public string StackName { get; init; } = string.Empty;

    public DockerComposeAction Action { get; init; }
}

// --- Networks ---

public sealed record DockerNetworkDto
{
    [StringLength(128)]
    public string NetworkId { get; init; } = string.Empty;
    [StringLength(200)]
    public string Name { get; init; } = string.Empty;
    [StringLength(64)]
    public string Driver { get; init; } = string.Empty;
    [StringLength(64)]
    public string Scope { get; init; } = string.Empty;
    [StringLength(500)]
    public string Project { get; init; } = string.Empty;
}

// --- Volumes ---

public sealed record DockerVolumeDto
{
    [StringLength(200)]
    public string Name { get; init; } = string.Empty;
    [StringLength(64)]
    public string Driver { get; init; } = string.Empty;
    [StringLength(500)]
    public string Mountpoint { get; init; } = string.Empty;
    [StringLength(500)]
    public string Project { get; init; } = string.Empty;
}

// --- Full heartbeat data ---

public sealed record DockerDataDto
{
    public List<DockerContainerDto> Containers { get; init; } = [];
    public List<DockerImageDto> Images { get; init; } = [];
    public List<DockerComposeStackDto> ComposeStacks { get; init; } = [];
    public List<DockerNetworkDto> Networks { get; init; } = [];
    public List<DockerVolumeDto> Volumes { get; init; } = [];
}

// --- Prune ---

public sealed record DockerPruneRequest
{
    public bool Containers { get; init; }
    public bool Images { get; init; }
    public bool Volumes { get; init; }
}

// --- Resource limits ---

public sealed record DockerResourceLimitsRequest
{
    [Required]
    [StringLength(64)]
    public string ContainerId { get; init; } = string.Empty;

    [Range(0, 1024)]
    public double CpuLimit { get; init; }

    [Range(0, 1048576)]
    public long MemoryLimitMb { get; init; }
}

// --- Compose file ---

public sealed record DockerComposeFileDto
{
    [StringLength(200)]
    public string StackName { get; init; } = string.Empty;
    [StringLength(50000)]
    public string Content { get; init; } = string.Empty;
}

public sealed record DockerComposeFileSaveRequest
{
    [Required]
    [StringLength(200)]
    public string StackName { get; init; } = string.Empty;

    [Required]
    [StringLength(50000)]
    public string Content { get; init; } = string.Empty;
}

// --- Shell ---

public sealed record DockerShellRequest
{
    [Required]
    [StringLength(64)]
    public string ContainerId { get; init; } = string.Empty;

    [StringLength(100)]
    public string Shell { get; init; } = "/bin/sh";
}

// --- Exec (single command) ---

public sealed record DockerExecRequest
{
    [Required]
    [StringLength(64)]
    public string ContainerId { get; init; } = string.Empty;

    [Required]
    [StringLength(1000)]
    public string Command { get; init; } = string.Empty;
}

// --- Environment Variables ---

public sealed record DockerEnvVarDto
{
    [StringLength(200)]
    public string Key { get; init; } = string.Empty;
    [StringLength(2000)]
    public string Value { get; init; } = string.Empty;
}

// --- File Browser ---

public sealed record DockerFileEntryDto
{
    [StringLength(500)]
    public string Name { get; init; } = string.Empty;
    [StringLength(32)]
    public string Type { get; init; } = string.Empty;
    [StringLength(50)]
    public string Size { get; init; } = string.Empty;
    [StringLength(32)]
    public string Permissions { get; init; } = string.Empty;
}

public sealed record DockerBrowseRequest
{
    [Required]
    [StringLength(64)]
    public string ContainerId { get; init; } = string.Empty;

    [Required]
    [StringLength(500)]
    public string Path { get; init; } = "/";
}

// --- Image Build ---

public sealed record DockerBuildRequest
{
    [Required]
    [StringLength(200)]
    public string ImageTag { get; init; } = string.Empty;

    [Required]
    [StringLength(100000)]
    public string DockerfileContent { get; init; } = string.Empty;
}
