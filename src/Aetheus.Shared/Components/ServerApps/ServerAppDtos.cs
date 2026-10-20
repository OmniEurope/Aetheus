// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.ServerApps;

public sealed record ServerAppDto
{
    public int Id { get; init; }
    public int ServerId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Type { get; init; }
    public ServerAppStatus Status { get; init; }
    public int? Port { get; init; }
    public string? Path { get; init; }
    public string Source { get; init; } = string.Empty;
    public DateTime InstalledAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record CreateServerAppRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [StringLength(50)]
    public string? Version { get; init; }

    [StringLength(50)]
    public string? Type { get; init; }

    [Range(1, 65535)]
    public int? Port { get; init; }

    [StringLength(500)]
    public string? Path { get; init; }

    [Required]
    [StringLength(50)]
    public string Source { get; init; } = string.Empty;
}

public sealed record UpdateServerAppRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [StringLength(50)]
    public string? Version { get; init; }

    public ServerAppStatus Status { get; init; }
    [Range(1, 65535)]
    public int? Port { get; init; }

    [StringLength(500)]
    public string? Path { get; init; }
}

/// <summary> The values the applications grid's checkable Source filter offers. The grid is loaded
/// page by page, so the sources present across every application of the server come from the API.
/// </summary>
public sealed record ServerAppFilterValuesDto
{
    public List<string> Sources { get; init; } = [];
}
