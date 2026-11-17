// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// One package of one project that a public advisory concerns, as the vulnerability watch (PLAN-004) holds
/// it. The advisory is the same for every organization; the match belongs to one project.
/// </summary>
public sealed record CveWatchMatchDto
{
    public long MatchId { get; init; }
    public int OrganizationId { get; init; }
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;

    /// <summary>The identifier in the advisory feed (<c>GHSA-xxxx-xxxx-xxxx</c>).</summary>
    public string AdvisoryId { get; init; } = string.Empty;

    /// <summary>The CVE identifier, when the advisory names one.</summary>
    public string? CveId { get; init; }

    public string? Summary { get; init; }

    public AnalysisSeverity Severity { get; init; }

    /// <summary>False when the source published no severity: <see cref="Severity"/> is then the assumed High.</summary>
    public bool SeverityPublished { get; init; }

    /// <summary>When CISA listed the vulnerability as exploited in the wild, null when it did not.</summary>
    public DateTime? ExploitedSince { get; init; }

    public string Ecosystem { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? FixedVersion { get; init; }
    public VulnerabilityMatchStatus Status { get; init; }
    public DateTime FirstMatchedAt { get; init; }

    /// <summary>Set once the package moved past the fix, left the inventory or the advisory was withdrawn.</summary>
    public DateTime? ClosedAt { get; init; }

    public DateTime? NotifiedAt { get; init; }
    public int? FindingId { get; init; }
    public AnalysisFindingStatus? FindingStatus { get; init; }

    /// <summary>R-14: the inventory the match was read from (branch head, release commit, SBOM, servers), so two
    /// matches of one package in one project read apart; null once the item left the inventory.</summary>
    public ProjectInventoryOrigin? InventoryOrigin { get; init; }

    /// <summary>The branch or commit of that inventory, when it has one.</summary>
    public string? InventoryRef { get; init; }

    /// <summary>What to do about it, null once the match is closed. On the list it comes from the advisory alone;
    /// the detail read checks the target version against the package's registry.</summary>
    public CveWatchRecommendationDto? Recommendation { get; init; }
}
