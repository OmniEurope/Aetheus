// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Shared.Components.Analysis;

/// <summary>One inventory the watch reads for a project: where it comes from, when it was read and what it really knows.</summary>
public sealed record CveWatchProjectInventoryDto
{
    public ProjectInventoryOrigin Origin { get; init; }

    /// <summary>The internal repository it was read from, null for an SBOM or a server.</summary>
    public string? RepositoryName { get; init; }

    /// <summary>The branch or tag read.</summary>
    public string? Ref { get; init; }

    public DateTime ReadAt { get; init; }
    public ProjectInventoryCoverage Coverage { get; init; }

    /// <summary>The stable gap codes of an incomplete inventory (<c>code:technology=count</c>, comma separated), for the page to translate.</summary>
    public string? Reason { get; init; }

    public int ItemCount { get; init; }
}

/// <summary>What the CVE watch says of one project: the open CVE by severity, what feeds the watch and how fresh it is.</summary>
public sealed record CveWatchProjectDto
{
    public int ProjectId { get; init; }

    /// <summary>Distinct open CVE (a closed match is left out).</summary>
    public int OpenCount { get; init; }

    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }

    /// <summary>Distinct open CVE that CISA lists as exploited.</summary>
    public int ExploitedCount { get; init; }

    /// <summary>Distinct open CVE with no published fix.</summary>
    public int WithoutFixCount { get; init; }

    /// <summary>When the watch last wrote a report for the project, null when it never did.</summary>
    public DateTime? LastWatchedAt { get; init; }

    public int? LastWatchRunId { get; init; }

    /// <summary>The inventories that feed the watch for this project, one per origin and repository.</summary>
    public List<CveWatchProjectInventoryDto> Inventories { get; init; } = [];

    public List<CveWatchSourceDto> Sources { get; init; } = [];
}
