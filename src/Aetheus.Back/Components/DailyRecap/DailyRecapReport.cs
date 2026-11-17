// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// What the recap tells about one local day. Every list comes from a table the platform already
/// fills; a section left out of <see cref="Sections"/> is not read at all and stays empty.
/// </summary>
internal sealed record DailyRecapReport
{
    public required DateOnly Day { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public required IReadOnlySet<DailyRecapSection> Sections { get; init; }
    public IReadOnlyList<RecapPipelineRun> PipelineRuns { get; init; } = [];
    public IReadOnlyList<RecapServer> Servers { get; init; } = [];
    public IReadOnlyList<RecapRelease> Releases { get; init; } = [];
    public IReadOnlyList<RecapBackup> Backups { get; init; } = [];
    public IReadOnlyList<RecapExpiring> Certificates { get; init; } = [];
    public IReadOnlyList<RecapExpiring> Secrets { get; init; } = [];
    public RecapFindings Findings { get; init; } = new(new Dictionary<AnalysisSeverity, int>(), 0);
    public IReadOnlyList<RecapMailServer> MailServers { get; init; } = [];
    public RecapCveWatch CveWatch { get; init; } = RecapCveWatch.Empty;

    /// <summary>The open CVEs of each project that has any. Unscoped as collected: the sender keeps, per recipient, the projects they may read.</summary>
    public IReadOnlyList<RecapProjectCve> CveProjects { get; init; } = [];

    /// <summary>R-41: the open CVEs of the host system of each server that has any. Unscoped as collected: the sender keeps, per recipient, the servers they may read.</summary>
    public IReadOnlyList<RecapServerCve> CveServers { get; init; } = [];

    /// <summary>The sources the CVE section owes attribution to, for the ecosystems the watch runs.</summary>
    public IReadOnlyList<VulnerabilityAttribution> CveSources { get; init; } = [];

    /// <summary>How many days ahead an expiring certificate or secret is reported.</summary>
    public int CertificateHorizonDays { get; init; }
    public int SecretHorizonDays { get; init; }

    public bool Has(DailyRecapSection section) => Sections.Contains(section);
}

/// <summary>A parent pipeline run that reached a final status during the day.</summary>
internal sealed record RecapPipelineRun(
    int RunId, string PipelineName, string? ProjectName, PipelineStatus Status, DateTime CompletedAtUtc, string? BranchName,
    int RunNumber = 0);

internal sealed record RecapOsUpdates(bool Probed, int PendingTotal, int PendingSecurity);

internal enum RecapDiskLevel
{
    Ok,
    Low,
    Critical
}

internal sealed record RecapDisk(double FreeGb, double TotalGb, double UsedPercent, RecapDiskLevel Level, DateTime MeasuredAtUtc);

internal sealed record RecapServer(
    int Id,
    string Name,
    ServerStatus Status,
    DateTime LastHeartbeatUtc,
    RecapOsUpdates? OsUpdates,
    AgentCompatibilityStatus AgentStatus,
    string AgentVersion,
    string TargetAgentVersion,
    RecapDisk? Disk);

/// <summary>A release promoted (<see cref="ReleaseStatus.Promoted"/>) or rolled back during the day.</summary>
internal sealed record RecapRelease(string ProjectName, string Version, ReleaseStatus Event, DateTime AtUtc);

internal sealed record RecapBackup(
    string PolicyName, string ProjectName, string ServerName, BackupRunStatus Status, DateTime FinishedAtUtc, string? Message);

/// <summary>A certificate (owner: its server) or a vault secret (owner: its vault) close to expiry.</summary>
internal sealed record RecapExpiring(string Name, string Owner, DateTime ExpiresAtUtc);

/// <summary>Findings first seen during the day, by severity, and findings resolved during it.</summary>
internal sealed record RecapFindings(IReadOnlyDictionary<AnalysisSeverity, int> NewBySeverity, int Resolved)
{
    public int NewTotal => NewBySeverity.Values.Sum();
}

/// <summary>
/// What the vulnerability watch holds, platform-wide like the findings: the open matches that are certain
/// (affected, finding not accepted or dismissed) by the severity their source published, the ones that
/// are exploited (KEV) or have no published severity, the ones still to verify, and what appeared or closed
/// during the day.
/// </summary>
internal sealed record RecapCveWatch(
    IReadOnlyDictionary<AnalysisSeverity, int> OpenBySeverity,
    int OpenExploited,
    int OpenUnrated,
    int OpenToVerify,
    int NewInDay,
    int ClosedInDay)
{
    public static RecapCveWatch Empty { get; } = new(new Dictionary<AnalysisSeverity, int>(), 0, 0, 0, 0, 0);

    /// <summary>Affected packages still open: every severity, and the ones with none published.</summary>
    public int OpenTotal => OpenBySeverity.Values.Sum() + OpenUnrated;

    public bool IsQuiet => OpenTotal == 0 && OpenToVerify == 0 && NewInDay == 0 && ClosedInDay == 0;
}

/// <summary>
/// The affected packages still open in one project (recette R-25): what the recap says to that project's
/// readers, per project, instead of mailing the same open CVEs every day. <see cref="BySeverity"/> counts the
/// ones whose source published a severity; <see cref="Unrated"/> the ones with none.
/// </summary>
internal sealed record RecapProjectCve(
    int ProjectId,
    string ProjectName,
    IReadOnlyDictionary<AnalysisSeverity, int> BySeverity,
    int Exploited,
    int Unrated)
{
    public int Open => BySeverity.Values.Sum() + Unrated;
}

/// <summary>R-41: the affected packages of one server's host system, counted like those of a project.</summary>
internal sealed record RecapServerCve(
    int ServerId,
    string ServerName,
    IReadOnlyDictionary<AnalysisSeverity, int> BySeverity,
    int Exploited,
    int Unrated)
{
    public int Open => BySeverity.Values.Sum() + Unrated;
}

internal sealed record RecapMailServer(
    string ServerName, bool PostfixRunning, bool DovecotRunning, int QueueSize, DateTime LastUpdatedUtc);
