// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisPlatformOptions
{
    public const string SectionName = "Analysis:Runtime";
    public int MaxConcurrentIngestions { get; set; } = 4;
    public int MaxConcurrentReportAdmissions { get; set; } = 1;
    public int ProjectDailyReportLimit { get; set; } = 1000;
    public long ProjectDailyBytesLimit { get; set; } = 2_147_483_648;
    public int FindingRetentionDays { get; set; } = 730;
    public int SbomRetentionDays { get; set; } = 365;
    public int LogRetentionDays { get; set; } = 30;
    public int EphemeralEnvironmentRetentionHours { get; set; } = 24;
    public int OperationalCheckIntervalMinutes { get; set; } = 60;
    public int ScannerManifestMaxAgeDays { get; set; } = 45;
    public int ContinuousSyncStaleHours { get; set; } = 24;
    public int OperationalAlertCooldownHours { get; set; } = 24;
    public int StorageWarningPercent { get; set; } = 80;

    /// <summary>A scanner reported unavailable or degraded is alerted only once it has been seen so for this long
    /// across health cycles (R-1): the host's daily reboot leaves Docker down for a couple of minutes, and the first
    /// cycle after the backend restarts reads that. Zero alerts on the first sight.</summary>
    public int ScannerUnavailableGraceMinutes { get; set; } = 15;

    internal bool IsValid() => HasValidIngestionLimits() && HasValidRetention() && HasValidOperationalChecks();

    private bool HasValidIngestionLimits() =>
        Within(MaxConcurrentIngestions, 1, 64)
        && Within(MaxConcurrentReportAdmissions, 1, 8)
        && MaxConcurrentReportAdmissions <= MaxConcurrentIngestions
        && ProjectDailyReportLimit > 0
        && ProjectDailyBytesLimit >= 1_048_576;

    private bool HasValidRetention() =>
        Within(FindingRetentionDays, 1, 3650)
        && Within(SbomRetentionDays, 1, 3650)
        && Within(LogRetentionDays, 1, 3650)
        && Within(EphemeralEnvironmentRetentionHours, 1, 168);

    private bool HasValidOperationalChecks() =>
        Within(OperationalCheckIntervalMinutes, 5, 1440)
        && Within(ScannerManifestMaxAgeDays, 1, 365)
        && Within(ContinuousSyncStaleHours, 1, 720)
        && Within(OperationalAlertCooldownHours, 1, 168)
        && Within(StorageWarningPercent, 1, 100)
        && Within(ScannerUnavailableGraceMinutes, 0, 1440);

    private static bool Within(int value, int minimum, int maximum) => value >= minimum && value <= maximum;
}
