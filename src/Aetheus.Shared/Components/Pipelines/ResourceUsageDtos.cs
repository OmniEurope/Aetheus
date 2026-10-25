// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>What reads a variable library or a vault (recette R-65, R-66).</summary>
public enum ResourceUsageKind
{
    /// <summary>A pipeline whose definition (after <c>extends:</c>) names the resource.</summary>
    Pipeline = 0,

    /// <summary>A monitored application whose web analytics keys live in the vault.</summary>
    MonitoredApp = 1
}

/// <summary>One reader of a library or a vault. A pipeline the caller may not read is returned with
/// <see cref="Id"/> 0 and an empty <see cref="Name"/>, so a resource read only by it never looks
/// unused.</summary>
public sealed record ResourceUsageUserDto
{
    public ResourceUsageKind Kind { get; init; }
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>The project of the reader; the monitoring page of a monitored application is under it.</summary>
    public int? ProjectId { get; init; }
}

/// <summary>For each library and vault the caller can read, who reads it. A resource absent from a
/// dictionary is read by nothing the instance knows of.</summary>
public sealed record ResourceUsageDto
{
    public Dictionary<int, List<ResourceUsageUserDto>> Libraries { get; init; } = [];
    public Dictionary<int, List<ResourceUsageUserDto>> Vaults { get; init; } = [];
}
