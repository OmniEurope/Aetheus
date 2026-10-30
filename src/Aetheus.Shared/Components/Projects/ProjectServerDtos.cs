// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Projects;

public sealed record ProjectServerDto
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int? ServerId { get; init; }
    public ProjectServerType Type { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int? Port { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    // Denormalized agent server info (null when Type == ExternalHost)
    public string? ServerName { get; init; }
    public string? ServerHostname { get; init; }
    public ServerStatus? ServerStatus { get; init; }

    /// <summary>Set when the server belongs to the project only because it is attached to one of the project's
    /// environments (R-83). Such a line has no project server link behind it (<see cref="Id"/> is 0): it cannot
    /// be edited or detached from the project, only from that environment.</summary>
    public int? ViaEnvironmentId { get; init; }

    public string? ViaEnvironmentName { get; init; }
}

public abstract record ProjectServerMutationRequest
{
    [Required]
    [StringLength(200)]
    public string DisplayName { get; init; } = string.Empty;

    [Required]
    [StringLength(500)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int? Port { get; init; }

    [StringLength(2000)]
    public string? Notes { get; init; }
}

public sealed record CreateProjectServerRequest : ProjectServerMutationRequest
{
    [Required]
    public ProjectServerType Type { get; init; }

    [Range(1, int.MaxValue)]
    public int? ServerId { get; init; }
}

public sealed record UpdateProjectServerRequest : ProjectServerMutationRequest;
