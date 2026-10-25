// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Servers;

public sealed record ServiceInfoDto
{
    [StringLength(200)]
    public string Name { get; init; } = string.Empty;
    public ServiceType Type { get; init; }
    [StringLength(100)]
    public string Status { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public bool IsManageable { get; init; }
    public bool IsInstalled { get; init; } = true;
}
