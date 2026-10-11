// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// Gathers the recap of one local day. Only the sections asked for are read, and each threshold is
/// the one the platform already uses elsewhere, so the e-mail says what the screens say.
/// </summary>
internal sealed class DailyRecapCollector(
    IDailyRecapRepository repo,
    IAgentCompatibilityPolicy compatibility,
    IConfiguration configuration)
{
    /// <summary>The runs listed for one day; on a busier day the e-mail says the list stops there.</summary>
    internal const int MaxPipelineRuns = 500;

    /// <summary>The Certbot page marks a certificate as expiring within 30 days; the recap uses the same horizon.</summary>
    internal const int CertificateHorizonDays = 30;

    public async Task<DailyRecapReport> CollectAsync(
        DateOnly day, TimeZoneInfo timeZone, IReadOnlySet<DailyRecapSection> sections, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(sections);
        var (startUtc, endUtc) = DailyRecapClock.DayWindowUtc(day, timeZone);
        // Same settings as SecretExpirationService and StorageAlertProvisioningService.
        var secretDays = Math.Max(1, configuration.GetValue("SecretExpiration:AlertDays", 14));

        var report = new DailyRecapReport
        {
            Day = day,
            GeneratedAtUtc = nowUtc,
            TimeZone = timeZone,
            Sections = sections,
            CertificateHorizonDays = CertificateHorizonDays,
            SecretHorizonDays = secretDays
        };

        if (sections.Contains(DailyRecapSection.Pipelines))
            report = report with { PipelineRuns = await repo.GetParentRunsCompletedAsync(startUtc, endUtc, MaxPipelineRuns, ct).ConfigureAwait(false) };
        if (sections.Contains(DailyRecapSection.Servers))
            report = report with { Servers = await CollectServersAsync(ct).ConfigureAwait(false) };
        if (sections.Contains(DailyRecapSection.Releases))
            report = report with { Releases = await repo.GetReleaseEventsAsync(startUtc, endUtc, ct).ConfigureAwait(false) };
        if (sections.Contains(DailyRecapSection.Backups))
            report = report with { Backups = await repo.GetBackupsFinishedAsync(startUtc, endUtc, ct).ConfigureAwait(false) };
        if (sections.Contains(DailyRecapSection.Certificates))
            report = report with { Certificates = await repo.GetCertificatesExpiringBeforeAsync(nowUtc.AddDays(CertificateHorizonDays), ct).ConfigureAwait(false) };
        if (sections.Contains(DailyRecapSection.Secrets))
            report = report with { Secrets = await repo.GetSecretsExpiringBeforeAsync(nowUtc.AddDays(secretDays), ct).ConfigureAwait(false) };
        if (sections.Contains(DailyRecapSection.Findings))
            report = report with { Findings = await repo.GetFindingChangesAsync(startUtc, endUtc, ct).ConfigureAwait(false) };
        if (sections.Contains(DailyRecapSection.Mail))
            report = report with { MailServers = await repo.GetMailServersAsync(ct).ConfigureAwait(false) };
        return report;
    }

    private async Task<IReadOnlyList<RecapServer>> CollectServersAsync(CancellationToken ct)
    {
        var warningPercent = Math.Clamp(configuration.GetValue("StorageMonitoring:DiskWarningPercent", 80d), 1d, 100d);
        var criticalPercent = Math.Clamp(configuration.GetValue("StorageMonitoring:DiskCriticalPercent", 90d), warningPercent, 100d);
        var facts = await repo.GetServerFactsAsync(ct).ConfigureAwait(false);
        var agents = (await repo.GetAgentCompatibilityFactsAsync(ct).ConfigureAwait(false))
            .ToDictionary(server => server.Id, compatibility.Evaluate);

        return facts.Select(server =>
        {
            var agent = agents.GetValueOrDefault(server.Id);
            return new RecapServer(
                server.Id,
                server.Name,
                server.Status,
                server.LastHeartbeatUtc,
                server.OsUpdates,
                agent?.Status ?? AgentCompatibilityStatus.Unknown,
                server.AgentVersion,
                agent?.TargetVersion ?? string.Empty,
                Disk(server, warningPercent, criticalPercent));
        }).ToList();
    }

    internal static RecapDisk? Disk(RecapServerFacts server, double warningPercent, double criticalPercent)
    {
        // A server that never reported a disk size has no free space to show, rather than a fake 0 GB.
        if (server is not { DiskUsedGb: { } used, DiskTotalGb: { } total, DiskMeasuredAtUtc: { } measured } || total <= 0)
            return null;
        var usedPercent = Math.Clamp(used / total * 100d, 0d, 100d);
        var level = usedPercent >= criticalPercent ? RecapDiskLevel.Critical
            : usedPercent >= warningPercent ? RecapDiskLevel.Low
            : RecapDiskLevel.Ok;
        return new RecapDisk(Math.Max(0d, total - used), total, usedPercent, level, measured);
    }
}
