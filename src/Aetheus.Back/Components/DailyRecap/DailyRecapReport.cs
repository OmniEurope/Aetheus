// SPDX-License-Identifier: EUPL-1.2
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

    /// <summary>How many days ahead an expiring certificate or secret is reported.</summary>
    public int CertificateHorizonDays { get; init; }
    public int SecretHorizonDays { get; init; }

    public bool Has(DailyRecapSection section) => Sections.Contains(section);
}

/// <summary>A parent pipeline run that reached a final status during the day.</summary>
internal sealed record RecapPipelineRun(
    int RunId, string PipelineName, string? ProjectName, PipelineStatus Status, DateTime CompletedAtUtc, string? BranchName);

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

internal sealed record RecapMailServer(
    string ServerName, bool PostfixRunning, bool DovecotRunning, int QueueSize, DateTime LastUpdatedUtc);
