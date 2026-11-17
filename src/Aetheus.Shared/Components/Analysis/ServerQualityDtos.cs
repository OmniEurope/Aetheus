// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>R-7: the quality of one server, from the vulnerabilities of its host system (never from a project's findings).</summary>
public sealed record ServerQualityDto
{
    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public int OrganizationId { get; init; }

    /// <summary>Whether the watch covers the server; only a watched server whose findings are computed has a grade.</summary>
    public HostVulnerabilityWatchState State { get; init; }

    public string? OsName { get; init; }

    /// <summary>The grade of the server (<see cref="ServerHostGrade"/>); null when nothing can be said yet (system not
    /// covered, findings not computed): never an A by default.</summary>
    public AnalysisGrade? Grade { get; init; }

    /// <summary>Every open vulnerability by severity, those without a published fix included.</summary>
    public AnalysisGradeFindingCountsDto Counts { get; init; } = new();

    /// <summary>The open vulnerabilities with no published fix, by severity (a subset of <see cref="Counts"/>).</summary>
    public AnalysisGradeFindingCountsDto CountsWithoutFix { get; init; } = new();

    /// <summary>The open vulnerabilities CISA lists as exploited (information: each is already critical or high).</summary>
    public int Exploited { get; init; }

    public DateTime? ReportedAt { get; init; }

    /// <summary>When the findings were last computed; the date of the last analysis.</summary>
    public DateTime? MatchedAt { get; init; }

    public int OpenCount => AnalysisFindingScore.Total(Counts);
    public int WithoutFixCount => AnalysisFindingScore.Total(CountsWithoutFix);
    public int FixableCount => OpenCount - WithoutFixCount;

    /// <summary>Medium, low and info vulnerabilities, the line of the grade block that reads "none: A, up to 5: B, more: C".</summary>
    public int OtherCount => Counts.Medium + Counts.Low + Counts.Info;
}
