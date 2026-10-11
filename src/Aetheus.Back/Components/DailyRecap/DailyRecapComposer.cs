// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// Turns a <see cref="DailyRecapReport"/> into a <see cref="RecapDocument"/>: which section comes
/// first, what each status is called and which colour it takes. Failures are listed before
/// successes, so the eye lands on what needs action. A section with nothing to tell and a figure
/// at zero are left out (recette R1-47): the e-mail lists only what happened.
/// </summary>
internal static class DailyRecapComposer
{
    /// <summary>A certificate or secret this close to expiry is shown in red rather than amber.</summary>
    private const int UrgentDays = 7;

    /// <param name="frontOrigin">The web front the reader opens, e.g. https://app.example.org; when it
    /// is not an absolute HTTP(S) URL the e-mail carries no settings link.</param>
    public static RecapDocument Compose(DailyRecapReport report, DailyRecapText t, string? frontOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(t);
        var date = t.Date(report.Day);
        var blocks = new List<RecapBlock?>();
        if (report.Has(DailyRecapSection.Pipelines)) blocks.Add(Pipelines(report, t));
        if (report.Has(DailyRecapSection.Servers)) blocks.Add(Servers(report, t));
        if (report.Has(DailyRecapSection.Releases)) blocks.Add(Releases(report, t));
        if (report.Has(DailyRecapSection.Backups)) blocks.Add(Backups(report, t));
        if (report.Has(DailyRecapSection.Certificates))
            blocks.Add(Expiring(report, t, report.Certificates, "CertificatesTitle", "CertificatesSummary", report.CertificateHorizonDays));
        if (report.Has(DailyRecapSection.Secrets))
            blocks.Add(Expiring(report, t, report.Secrets, "SecretsTitle", "SecretsSummary", report.SecretHorizonDays));
        if (report.Has(DailyRecapSection.Findings)) blocks.Add(Findings(report, t));
        if (report.Has(DailyRecapSection.Mail)) blocks.Add(Mail(report, t));
        var shown = blocks.OfType<RecapBlock>().ToList();
        if (shown.Count == 0) shown.Add(new RecapBlock(t["QuietDayTitle"], t["QuietDaySummary"], []));

        return new RecapDocument(
            t.Format("Subject", date),
            t.Format("Heading", date),
            t.Format("Intro", report.TimeZone.Id, t.Moment(report.GeneratedAtUtc, report.TimeZone)),
            Tiles(report, t),
            shown,
            t["Footer"],
            t["LabelSeparator"],
            SettingsLink(frontOrigin, t));
    }

    private static RecapLink? SettingsLink(string? frontOrigin, DailyRecapText t)
    {
        if (!Uri.TryCreate(frontOrigin?.Trim(), UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp))
            return null;
        return new RecapLink(t["SettingsLink"], origin.GetLeftPart(UriPartial.Authority) + "/admin/settings?tab=daily-recap");
    }

    private static List<RecapTile> Tiles(DailyRecapReport report, DailyRecapText t)
    {
        var tiles = new List<RecapTile>();
        if (report.Has(DailyRecapSection.Pipelines))
        {
            var succeeded = report.PipelineRuns.Count(run => run.Status == PipelineStatus.Success);
            var failed = report.PipelineRuns.Count(run => PipelineTone(run.Status) == RecapTone.Danger);
            if (succeeded > 0) tiles.Add(new RecapTile(t["TileRunsSucceeded"], Count(succeeded, t), RecapTone.Success));
            if (failed > 0) tiles.Add(new RecapTile(t["TileRunsFailed"], Count(failed, t), RecapTone.Danger));
        }
        if (report.Has(DailyRecapSection.Servers))
        {
            var watched = report.Servers.Where(server => server.Status != ServerStatus.Disabled).ToList();
            var online = watched.Count(server => server.Status == ServerStatus.Online);
            var tone = online == watched.Count ? RecapTone.Success : RecapTone.Danger;
            if (watched.Count > 0) tiles.Add(new RecapTile(t["TileServersOnline"], t.Format("Ratio", online, watched.Count), tone));
        }
        return tiles;
    }

    private static RecapBlock? Pipelines(DailyRecapReport report, DailyRecapText t)
    {
        var runs = report.PipelineRuns;
        if (runs.Count == 0) return null;
        var succeeded = runs.Count(run => run.Status == PipelineStatus.Success);
        var failed = runs.Count(run => PipelineTone(run.Status) == RecapTone.Danger);
        var items = runs
            .OrderBy(run => ToneRank(PipelineTone(run.Status)))
            .ThenBy(run => run.CompletedAtUtc)
            .Select(run => new RecapItem(
                run.PipelineName,
                Join(run.ProjectName, run.BranchName),
                new RecapBadge(PipelineTone(run.Status), t[$"Run{run.Status}"]),
                [new RecapLine(t["FinishedAt"], null, t.Format("RunNumber", run.RunId, t.Time(run.CompletedAtUtc, report.TimeZone)))]))
            .ToList();
        var summary = t.Format("PipelinesSummary", runs.Count, succeeded, failed, runs.Count - succeeded - failed);
        var note = runs.Count >= DailyRecapCollector.MaxPipelineRuns ? t.Format("PipelinesTruncated", DailyRecapCollector.MaxPipelineRuns) : t["PipelinesParentsOnly"];
        return new RecapBlock(t["PipelinesTitle"], summary, items, note);
    }

    private static RecapBlock? Servers(DailyRecapReport report, DailyRecapText t)
    {
        if (report.Servers.Count == 0) return null;
        var items = report.Servers
            .OrderBy(server => ToneRank(ServerTone(server)))
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .Select(server => new RecapItem(
                server.Name,
                server.Status == ServerStatus.Online ? null : t.Format("LastSeen", t.Moment(server.LastHeartbeatUtc, report.TimeZone)),
                new RecapBadge(StatusTone(server.Status), t[$"Server{server.Status}"]),
                [OsUpdates(server, t), Agent(server, t), Disk(server, t)]))
            .ToList();
        var attention = report.Servers.Count(server => ServerTone(server) is RecapTone.Danger or RecapTone.Warning);
        var summary = t.Format("ServersSummary", report.Servers.Count, attention);
        return new RecapBlock(t["ServersTitle"], summary, items);
    }

    private static RecapLine OsUpdates(RecapServer server, DailyRecapText t) => server.OsUpdates switch
    {
        null or { Probed: false } => new RecapLine(t["OsUpdates"], new RecapBadge(RecapTone.Neutral, t["Unknown"]), t["OsUpdatesUnknown"]),
        { PendingTotal: 0 } => new RecapLine(t["OsUpdates"], new RecapBadge(RecapTone.Success, t["UpToDate"]), t["OsUpdatesNone"]),
        var updates => new RecapLine(
            t["OsUpdates"],
            new RecapBadge(updates.PendingSecurity > 0 ? RecapTone.Danger : RecapTone.Warning, t["UpdatesPending"]),
            t.Format("OsUpdatesPending", updates.PendingTotal, updates.PendingSecurity))
    };

    private static RecapLine Agent(RecapServer server, DailyRecapText t)
    {
        var version = string.IsNullOrWhiteSpace(server.AgentVersion) ? t["Unknown"] : server.AgentVersion;
        var text = server.AgentStatus is AgentCompatibilityStatus.UpdateRecommended or AgentCompatibilityStatus.UpdateRequired
            && server.TargetAgentVersion.Length > 0
            ? t.Format("AgentVersionTarget", version, server.TargetAgentVersion)
            : t.Format("AgentVersion", version);
        return new RecapLine(t["Agent"], new RecapBadge(AgentTone(server.AgentStatus), t[$"Agent{server.AgentStatus}"]), text);
    }

    private static RecapLine Disk(RecapServer server, DailyRecapText t) => server.Disk is not { } disk
        ? new RecapLine(t["Disk"], new RecapBadge(RecapTone.Neutral, t["Unknown"]), t["DiskUnknown"])
        : new RecapLine(
            t["Disk"],
            new RecapBadge(DiskTone(disk.Level), t[$"Disk{disk.Level}"]),
            t.Format("DiskFree", t.Number(disk.FreeGb), t.Number(disk.TotalGb), t.Number(100d - disk.UsedPercent)));

    private static RecapBlock? Releases(DailyRecapReport report, DailyRecapText t)
    {
        if (report.Releases.Count == 0) return null;
        var items = report.Releases
            .Select(release => new RecapItem(
                $"{release.ProjectName} {release.Version}",
                null,
                release.Event == ReleaseStatus.Promoted
                    ? new RecapBadge(RecapTone.Success, t["ReleasePromoted"])
                    : new RecapBadge(RecapTone.Warning, t["ReleaseRolledBack"]),
                [new RecapLine(t["At"], null, t.Time(release.AtUtc, report.TimeZone))]))
            .ToList();
        var summary = t.Format("ReleasesSummary", items.Count);
        return new RecapBlock(t["ReleasesTitle"], summary, items);
    }

    private static RecapBlock? Backups(DailyRecapReport report, DailyRecapText t)
    {
        if (report.Backups.Count == 0) return null;
        var failed = report.Backups.Count(backup => backup.Status == BackupRunStatus.Failed);
        var items = report.Backups
            .OrderBy(backup => backup.Status == BackupRunStatus.Failed ? 0 : 1)
            .ThenBy(backup => backup.FinishedAtUtc)
            .Select(backup =>
            {
                var lines = new List<RecapLine> { new(t["FinishedAt"], null, t.Time(backup.FinishedAtUtc, report.TimeZone)) };
                if (backup.Status == BackupRunStatus.Failed && !string.IsNullOrWhiteSpace(backup.Message))
                    lines.Add(new RecapLine(t["Reason"], null, backup.Message.Trim()));
                return new RecapItem(
                    backup.PolicyName,
                    t.Format("BackupWhere", backup.ProjectName, backup.ServerName),
                    backup.Status == BackupRunStatus.Failed
                        ? new RecapBadge(RecapTone.Danger, t["BackupFailed"])
                        : new RecapBadge(RecapTone.Success, t["BackupSucceeded"]),
                    lines);
            })
            .ToList();
        var summary = t.Format("BackupsSummary", items.Count, items.Count - failed, failed);
        return new RecapBlock(t["BackupsTitle"], summary, items);
    }

    private static RecapBlock? Expiring(
        DailyRecapReport report, DailyRecapText t, IReadOnlyList<RecapExpiring> entries,
        string titleKey, string summaryKey, int horizonDays)
    {
        if (entries.Count == 0) return null;
        var items = entries.Select(entry =>
        {
            var days = (int)Math.Floor((entry.ExpiresAtUtc - report.GeneratedAtUtc).TotalDays);
            var badge = days < 0
                ? new RecapBadge(RecapTone.Danger, t["Expired"])
                : new RecapBadge(days <= UrgentDays ? RecapTone.Danger : RecapTone.Warning, t.Format("ExpiresInDays", days));
            return new RecapItem(entry.Name, entry.Owner, badge,
                [new RecapLine(t["ExpiresOn"], null, t.Moment(entry.ExpiresAtUtc, report.TimeZone))]);
        }).ToList();
        var summary = t.Format(summaryKey, items.Count, horizonDays);
        return new RecapBlock(t[titleKey], summary, items);
    }

    private static RecapBlock? Findings(DailyRecapReport report, DailyRecapText t)
    {
        var findings = report.Findings;
        if (findings.NewTotal == 0 && findings.Resolved == 0) return null;
        var lines = Enum.GetValues<AnalysisSeverity>()
            .OrderByDescending(severity => severity)
            .Where(severity => findings.NewBySeverity.GetValueOrDefault(severity) > 0)
            .Select(severity => new RecapLine(
                t[$"Severity{severity}"],
                new RecapBadge(severity >= AnalysisSeverity.High ? RecapTone.Danger : severity == AnalysisSeverity.Medium ? RecapTone.Warning : RecapTone.Neutral,
                    t[$"Severity{severity}"]),
                t.Format("FindingsNewCount", findings.NewBySeverity[severity])))
            .ToList();
        var items = lines.Count == 0 ? [] : new List<RecapItem> { new(t["FindingsNewTitle"], null, null, lines) };
        var summary = t.Format("FindingsSummary", findings.NewTotal, findings.Resolved);
        return new RecapBlock(t["FindingsTitle"], summary, items);
    }

    private static RecapBlock? Mail(DailyRecapReport report, DailyRecapText t)
    {
        if (report.MailServers.Count == 0) return null;
        var items = report.MailServers.Select(mail => new RecapItem(
            mail.ServerName,
            t.Format("ReportedAt", t.Moment(mail.LastUpdatedUtc, report.TimeZone)),
            mail.PostfixRunning && mail.DovecotRunning
                ? new RecapBadge(RecapTone.Success, t["MailRunning"])
                : new RecapBadge(RecapTone.Danger, t["MailStopped"]),
            MailLines(mail, t))).ToList();
        var summary = t.Format("MailSummary", items.Count);
        return new RecapBlock(t["MailTitle"], summary, items);
    }

    /// <summary>An empty queue is not news: the line appears only when messages wait.</summary>
    private static List<RecapLine> MailLines(RecapMailServer mail, DailyRecapText t)
    {
        var lines = new List<RecapLine>
        {
            new("Postfix", Running(mail.PostfixRunning, t), string.Empty),
            new("Dovecot", Running(mail.DovecotRunning, t), string.Empty)
        };
        if (mail.QueueSize > 0) lines.Add(new RecapLine(t["MailQueue"], null, t.Format("MailQueueCount", mail.QueueSize)));
        return lines;
    }

    private static RecapBadge Running(bool running, DailyRecapText t) =>
        running ? new RecapBadge(RecapTone.Success, t["ServiceRunning"]) : new RecapBadge(RecapTone.Danger, t["ServiceStopped"]);

    internal static RecapTone PipelineTone(PipelineStatus status) => status switch
    {
        PipelineStatus.Success => RecapTone.Success,
        PipelineStatus.Failed or PipelineStatus.Rejected or PipelineStatus.RolledBack => RecapTone.Danger,
        PipelineStatus.Partial => RecapTone.Warning,
        _ => RecapTone.Neutral
    };

    private static RecapTone StatusTone(ServerStatus status) => status switch
    {
        ServerStatus.Online => RecapTone.Success,
        ServerStatus.Offline => RecapTone.Danger,
        _ => RecapTone.Neutral
    };

    private static RecapTone AgentTone(AgentCompatibilityStatus status) => status switch
    {
        AgentCompatibilityStatus.UpToDate => RecapTone.Success,
        AgentCompatibilityStatus.UpdateRecommended => RecapTone.Warning,
        AgentCompatibilityStatus.UpdateRequired => RecapTone.Danger,
        _ => RecapTone.Neutral
    };

    private static RecapTone DiskTone(RecapDiskLevel level) => level switch
    {
        RecapDiskLevel.Critical => RecapTone.Danger,
        RecapDiskLevel.Low => RecapTone.Warning,
        _ => RecapTone.Success
    };

    /// <summary>The worst colour of a server, which orders the list: a disabled server is not a problem.</summary>
    private static RecapTone ServerTone(RecapServer server)
    {
        if (server.Status == ServerStatus.Disabled) return RecapTone.Neutral;
        var tones = new[]
        {
            StatusTone(server.Status),
            AgentTone(server.AgentStatus),
            server.Disk is { } disk ? DiskTone(disk.Level) : RecapTone.Neutral,
            server.OsUpdates is { Probed: true, PendingTotal: > 0 } updates
                ? updates.PendingSecurity > 0 ? RecapTone.Danger : RecapTone.Warning
                : RecapTone.Success
        };
        return tones.MinBy(ToneRank);
    }

    private static int ToneRank(RecapTone tone) => tone switch
    {
        RecapTone.Danger => 0,
        RecapTone.Warning => 1,
        RecapTone.Neutral => 2,
        _ => 3
    };

    private static string Count(int value, DailyRecapText t) => value.ToString(t.Culture);

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return present.Count == 0 ? null : string.Join(" · ", present);
    }
}
