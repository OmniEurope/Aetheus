// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Projects;

/// <summary>A server the project deployed to, with what the project's libraries say lives there.</summary>
public sealed record ProjectDeletionServerDto
{
    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public ProjectDeletionServerState State { get; init; }
    public List<ProjectDeletionEnvironmentDto> Environments { get; init; } = [];

    /// <summary>Backup policies of the project whose archives this server keeps.</summary>
    public int BackupPolicyCount { get; init; }

    /// <summary>Agent capabilities a removal on this server needs and the agent does not declare
    /// (<c>apache.manage</c> for virtual hosts, <c>certbot.manage</c> for certificates): what they cover stays.</summary>
    public List<string> MissingCapabilities { get; init; } = [];
}

/// <summary>The identity of one environment of the project on a server, read from its libraries.</summary>
public sealed record ProjectDeletionEnvironmentDto
{
    public string Name { get; init; } = string.Empty;
    public string? ComposeProject { get; init; }
    public List<string> Hosts { get; init; } = [];
    public string? StateDirectory { get; init; }

    /// <summary>Where the deployment pipelines unpack the repository copy of this environment on the server
    /// (<c>/var/lib/aetheus-agent/.deployments/&lt;Compose project&gt;</c>), derived from <see cref="ComposeProject"/>; null
    /// when the libraries name no Compose project. Unlike <see cref="StateDirectory"/>, which only a blue-green
    /// deployment creates, every deployment writes it.</summary>
    public string? DeploymentDirectory { get; init; }

    /// <summary>The blue-green upstream file (<c>UPSTREAM_CONF</c>), in <c>/etc/apache2/sites-available</c>.</summary>
    public string? UpstreamConfiguration { get; init; }
}

/// <summary>A value of the project's libraries that Aetheus will not use to find or remove anything,
/// shown so the user knows what may stay behind.</summary>
public sealed record ProjectDeletionRefusedValueDto(string Environment, string Key, string Value, ProjectDeletionRefusal Reason);

/// <summary>One resource of a server, with what the agent said of it.</summary>
public sealed record ProjectServerResourceDto(
    ProjectServerResourceKind Kind,
    string Name,
    string Scope,
    string? Environment,
    ProjectServerResourceOutcome Outcome,
    string? Reason);

/// <summary>Something the agent could not read (so it cannot say whether a resource exists).</summary>
public sealed record ProjectServerUnreadableDto(ProjectServerResourceKind Kind, string Scope, string Reason);

/// <summary>A discovery or removal task of a project deletion, as far as it went.</summary>
public sealed record ProjectServerResourceTaskDto
{
    public int TaskId { get; init; }
    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public ProjectServerTaskPhase Phase { get; init; }
    public ProjectServerTaskState State { get; init; }
    public List<ProjectServerResourceDto> Resources { get; init; } = [];
    public List<ProjectServerUnreadableDto> Unreadable { get; init; } = [];

    /// <summary>Why a failed task failed, when it is not said resource by resource.</summary>
    public string? Failure { get; init; }
}

/// <summary>Removes, on each server, what the given discoveries found. Volumes hold data (a database):
/// <see cref="AcknowledgeVolumeDataLoss"/> must say the user accepted losing it.</summary>
public sealed record ProjectServerCleanupRequest
{
    [Required]
    [MaxLength(ProjectServerResourceLimits.MaxTaskIds)]
    public List<int> DiscoveryTaskIds { get; init; } = [];

    public bool AcknowledgeVolumeDataLoss { get; init; }
}

/// <summary>Bounds of the project deletion contract.</summary>
public static class ProjectServerResourceLimits
{
    public const int MaxTaskIds = 200;
}
