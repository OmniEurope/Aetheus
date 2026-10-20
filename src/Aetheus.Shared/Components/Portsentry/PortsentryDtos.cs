// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Portsentry;

public sealed record PortsentryDataDto
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    [StringLength(100)]
    public string Version { get; init; } = string.Empty;
    [StringLength(20)]
    public string Mode { get; init; } = string.Empty;
    [StringLength(500)]
    public string TcpPorts { get; init; } = string.Empty;
    [StringLength(500)]
    public string UdpPorts { get; init; } = string.Empty;
    public int BlockedCount { get; init; }
    public List<PortsentryBlockedIpDto> BlockedIps { get; init; } = [];
}

public sealed record PortsentryBlockedIpDto
{
    [StringLength(45)]
    public string IpAddress { get; init; } = string.Empty;
    [StringLength(20)]
    public string Protocol { get; init; } = string.Empty;
    public DateTime BlockedAt { get; init; }
    [StringLength(1000)]
    public string Reason { get; init; } = string.Empty;
}

public sealed record PortsentryActionRequest
{
    [Required]
    public PortsentryAction Action { get; init; }
}

public sealed record PortsentrySetupRequest
{
    [Required]
    [StringLength(20)]
    public string Mode { get; init; } = "atcp";

    [StringLength(500)]
    public string TcpPorts { get; init; } = "1,11,15,79,111,119,143,540,635,1080,1524,2000,5742,6667,12345,12346,20034,27665,31337,32771,32772,32773,32774,40421,49724,54320";

    [StringLength(500)]
    public string UdpPorts { get; init; } = "1,7,9,69,161,162,513,635,640,641,700,32770,32771,32772,32773,32774,31337,54321";
}

public sealed record PortsentryUnblockRequest
{
    [Required]
    [StringLength(45)]
    public string IpAddress { get; init; } = string.Empty;
}

public sealed record PortsentryLogRequest
{
    [Range(1, 5000)]
    public int Lines { get; init; } = 100;
}

public sealed record PortsentryWhitelistIpDto
{
    public int Id { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed record AddPortsentryWhitelistRequest
{
    [Required]
    [StringLength(45)]
    public string IpAddress { get; init; } = string.Empty;

    [StringLength(200)]
    public string Description { get; init; } = string.Empty;
}

/// <summary> The values the blocked IPs grid's checkable Protocol filter offers. The grid is loaded
/// page by page, so the protocols present across every blocked IP of the server come from the API.
/// </summary>
public sealed record PortsentryFilterValuesDto
{
    public List<string> Protocols { get; init; } = [];
}
