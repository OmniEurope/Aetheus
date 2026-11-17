// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// One public advisory with every project the caller may read that it concerns, as the quality page lists
/// the watch: by CVE, then by project. The severity and the exploitation are the advisory's, the same
/// for every project.
/// </summary>
public sealed record CveWatchAdvisoryGroupDto
{
    /// <summary>The identifier in the advisory feed (<c>GHSA-xxxx-xxxx-xxxx</c>), unique.</summary>
    public string AdvisoryId { get; init; } = string.Empty;

    public string? CveId { get; init; }
    public string? Summary { get; init; }
    public AnalysisSeverity Severity { get; init; }

    /// <summary>False when the source published no severity: <see cref="Severity"/> is then the assumed High.</summary>
    public bool SeverityPublished { get; init; }

    public DateTime? ExploitedSince { get; init; }

    /// <summary>One match per project and package, the caller's readable projects only.</summary>
    public List<CveWatchMatchDto> Matches { get; init; } = [];
}

/// <summary>How fresh one vulnerability source is. The text of a failure is not exposed: it is the administrator's.</summary>
public sealed record CveWatchSourceDto
{
    /// <summary>The feed key: <c>osv:NuGet</c>, <c>kev</c>, <c>eol:dotnet</c>.</summary>
    public string Source { get; init; } = string.Empty;

    public DateTime? LastSuccessAt { get; init; }
    public DateTime? LastAttemptAt { get; init; }
    public DateTime? LastErrorAt { get; init; }

    /// <summary>True when the source never succeeded or not within the freshness threshold: no CVE found
    /// is then no proof of anything.</summary>
    public bool IsStale { get; init; }
}

/// <summary>A project the caller may read whose inventory is not complete, so that "no CVE" proves nothing for it.</summary>
public sealed record CveWatchUncoveredProjectDto
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public int OrganizationId { get; init; }
    public string OrganizationName { get; init; } = string.Empty;

    /// <summary>The best coverage any of its inventories has: <see cref="ProjectInventoryCoverage.None"/>
    /// when none was ever read.</summary>
    public ProjectInventoryCoverage Coverage { get; init; }
}

/// <summary>The figures of the CVE watch tab, over every project the caller may read, closed matches left out.</summary>
public sealed record CveWatchOverviewDto
{
    /// <summary>Distinct open CVE of critical severity.</summary>
    public int CriticalCount { get; init; }

    /// <summary>Distinct open CVE that CISA lists as exploited.</summary>
    public int ExploitedCount { get; init; }

    /// <summary>Distinct open CVE with no published fix for at least one project.</summary>
    public int WithoutFixCount { get; init; }

    public List<CveWatchUncoveredProjectDto> UncoveredProjects { get; init; } = [];
    public List<CveWatchSourceDto> Sources { get; init; } = [];
}

/// <summary>One report the CVE watch wrote (scanner <c>cve-watch</c>, one per project and category): its own run.</summary>
public sealed record CveWatchRunDto
{
    /// <summary>The number of the run: the identifier of the report, unique.</summary>
    public int ReportId { get; init; }

    public int OrganizationId { get; init; }
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public AnalysisCategory Category { get; init; }
    public DateTime WrittenAt { get; init; }

    /// <summary>Distinct findings the report lists.</summary>
    public int FindingCount { get; init; }

    /// <summary>Findings the report lists for the first time (or again after a fix).</summary>
    public int NewFindingCount { get; init; }

    /// <summary>Distinct packages the report names.</summary>
    public int PackageCount { get; init; }

    /// <summary>The findings, the newest first then the worst, bounded; <see cref="FindingCount"/> counts them all.</summary>
    public List<CveWatchRunFindingDto> Findings { get; init; } = [];

    public List<CveWatchSourceDto> Sources { get; init; } = [];
}

public sealed record CveWatchRunFindingDto
{
    public int FindingId { get; init; }
    public string Title { get; init; } = string.Empty;
    public AnalysisSeverity Severity { get; init; }
    public AnalysisFindingStatus Status { get; init; }
    public bool IsNew { get; init; }
    public List<string> Packages { get; init; } = [];
}
