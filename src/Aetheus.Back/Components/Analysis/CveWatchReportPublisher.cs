// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Publishes what the vulnerability matches of one project hold as findings, through a report that belongs to
/// no run (scanner <c>cve-watch</c>, one per category). A report is written only when the set of findings
/// changes: a pass that finds nothing new publishes nothing and costs no quota. The findings are the ones
/// Analysis already governs, so decisions, deadlines, audit and closure are reused: a CVE the report no
/// longer lists is swept as fixed by the existing closure, and one that comes back reopens the same finding
/// with the decision still standing. The report is not evaluated: it has no run, so it cannot enter the gate
/// or the grade of one.
/// </summary>
internal sealed class CveWatchReportPublisher(
    IAnalysisRepository analysis,
    CveWatchReportRepository watch,
    IDbTransactionScope transaction,
    TimeProvider timeProvider,
    IOptions<AnalysisPlatformOptions> platformOptions)
{
    private static readonly AnalysisCategory[] Categories = [AnalysisCategory.Dependencies, AnalysisCategory.Container];

    private readonly AnalysisPlatformOptions _platformOptions = platformOptions.Value;

    /// <summary>Returns the number of reports published, zero when nothing changed.</summary>
    public async Task<int> PublishProjectAsync(int projectId, CancellationToken ct)
    {
        var organizationId = await analysis.GetProjectOrganizationIdAsync(projectId, ct).ConfigureAwait(false);
        if (organizationId is null) return 0;
        var drafts = CveWatchFindings.Draft(projectId, await watch.GetOpenMatchesAsync(projectId, ct).ConfigureAwait(false));
        var published = 0;
        foreach (var category in Categories)
        {
            var wanted = drafts.FindAll(draft => draft.Category == category);
            if (!await HasChangedAsync(projectId, category, wanted, ct).ConfigureAwait(false)) continue;
            await PublishAsync(organizationId.Value, projectId, category, wanted, ct).ConfigureAwait(false);
            published++;
        }

        await LinkAsync(projectId, drafts, ct).ConfigureAwait(false);
        return published;
    }

    /// <summary>
    /// Points every open match at its finding, also those that joined a finding that already existed (a second
    /// version of the same package) and so changed no report. A link that fails is made at the next pass.
    /// </summary>
    private async Task LinkAsync(int projectId, List<CveWatchFindingDraft> drafts, CancellationToken ct)
    {
        var findings = await analysis.GetFindingsByFingerprintsAsync(
            projectId, [.. drafts.Select(draft => draft.Fingerprint)], ct).ConfigureAwait(false);
        var linked = new Dictionary<long, int>();
        foreach (var draft in drafts)
        {
            if (!findings.TryGetValue(draft.Fingerprint, out var finding)) continue;
            foreach (var match in draft.Matches) linked[match.MatchId] = finding.Id;
        }

        await watch.LinkMatchesAsync(projectId, linked, ct).ConfigureAwait(false);
    }

    private async Task<bool> HasChangedAsync(int projectId, AnalysisCategory category, List<CveWatchFindingDraft> wanted, CancellationToken ct)
    {
        var existing = await analysis.GetFindingsByFingerprintsAsync(
            projectId, [.. wanted.Select(draft => draft.Fingerprint)], ct).ConfigureAwait(false);
        var missing = wanted.Exists(draft => !existing.TryGetValue(draft.Fingerprint, out var finding)
            || finding.Status == AnalysisFindingStatus.Fixed
            || finding.Confidence != draft.Confidence
            || finding.Severity < draft.Severity);
        if (missing) return true;
        var active = await watch.GetActiveFingerprintsAsync(projectId, category, ct).ConfigureAwait(false);
        active.ExceptWith(wanted.Select(draft => draft.Fingerprint));
        return active.Count > 0;
    }

    private async Task PublishAsync(int organizationId, int projectId, AnalysisCategory category, List<CveWatchFindingDraft> wanted, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var content = string.Join('\n', wanted.Select(draft => $"{draft.Fingerprint}:{draft.Severity}:{draft.Confidence}"));
        var size = Encoding.UTF8.GetByteCount(content);
        await EnforceQuotaAsync(projectId, size, now, ct).ConfigureAwait(false);
        var report = new AnalysisReport
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            ScannerKey = CveWatchFindings.ScannerKey,
            ScannerName = CveWatchFindings.ScannerName,
            ScannerVersion = CveWatchFindings.ScannerVersion,
            Category = category,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.NativeJson,
            ContentHash = CveWatchFindings.Hash($"{category}\n{content}"),
            PayloadHash = CveWatchFindings.Hash($"{category}\n{content}"),
            ContentSize = size,
            StartedAt = now,
            CompletedAt = now
        };

        async Task PersistAsync()
        {
            // Governance first: a deadline that passed reopens its finding before the report speaks of it.
            await analysis.ReopenExpiredFindingDecisionsAsync(projectId, now, ct).ConfigureAwait(false);
            var findings = new Dictionary<string, AnalysisFinding>(
                await analysis.GetFindingsByFingerprintsAsync(projectId, [.. wanted.Select(draft => draft.Fingerprint)], ct).ConfigureAwait(false),
                StringComparer.Ordinal);
            foreach (var draft in wanted) Apply(organizationId, projectId, draft, findings, report, now);
            await analysis.AddReportAsync(report, ct).ConfigureAwait(false);
            await analysis.MarkMissingFindingsFixedAsync(
                projectId, CveWatchFindings.ScannerKey, category, null, [.. wanted.Select(draft => draft.Fingerprint)], now, ct).ConfigureAwait(false);
        }

        if (transaction.IsRelational)
            await transaction.ExecuteInTransactionAsync(PersistAsync, ct).ConfigureAwait(false);
        else
            await PersistAsync().ConfigureAwait(false);
    }

    private static void Apply(
        int organizationId, int projectId, CveWatchFindingDraft draft,
        Dictionary<string, AnalysisFinding> findings, AnalysisReport report, DateTime now)
    {
        var isNew = true;
        if (!findings.TryGetValue(draft.Fingerprint, out var finding))
        {
            finding = new AnalysisFinding
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Fingerprint = draft.Fingerprint,
                FingerprintVersion = 2,
                RuleId = draft.RuleId,
                Category = draft.Category,
                FirstSeenAt = now
            };
            findings.Add(draft.Fingerprint, finding);
        }
        else
        {
            isNew = false;
            if (finding.Status == AnalysisFindingStatus.Fixed)
            {
                // Back after a fix: the decision still standing applies again, otherwise it is open.
                finding.Status = AnalysisReportNormalizer.StandingDecisionStatus(finding, now) ?? AnalysisFindingStatus.Open;
                finding.ResolvedAt = null;
                isNew = true;
            }
        }

        // A finding filed before its ecosystem had its own category moves: left behind, the sweep of its old category
        // would close it at every pass and this report would reopen it.
        finding.Category = draft.Category;
        finding.LastSeenAt = now;
        finding.Severity = isNew || draft.Severity > finding.Severity ? draft.Severity : finding.Severity;
        finding.Confidence = draft.Confidence;
        finding.Title = draft.Title;
        finding.Message = draft.Message;
        finding.HelpUri = draft.HelpUri;

        foreach (var place in draft.Matches.DistinctBy(match => (match.Ecosystem, match.PackageName, match.Version)))
        {
            report.Occurrences.Add(new AnalysisFindingOccurrence
            {
                AnalysisFinding = finding,
                LocationHash = CveWatchFindings.Hash($"{draft.Fingerprint}|{place.Ecosystem}|{place.PackageName}|{place.Version}"),
                ToolName = CveWatchFindings.ScannerName,
                ScannerKey = CveWatchFindings.ScannerKey,
                RuleId = draft.RuleId,
                Symbol = place.PackageName,
                Message = $"{place.PackageName} {place.Version}: {(place.Status == VulnerabilityMatchStatus.Affected ? "affected" : "to verify")}",
                IsNew = isNew
            });
        }
    }

    /// <summary>The quota of every report of the project (1,000 a day by default): over it, the work stays pending.</summary>
    private async Task EnforceQuotaAsync(int projectId, int incomingBytes, DateTime now, CancellationToken ct)
    {
        var usage = await analysis.GetProjectIngestUsageAsync(projectId, now.AddDays(-1), ct).ConfigureAwait(false);
        if (usage.Count >= Math.Clamp(_platformOptions.ProjectDailyReportLimit, 1, 100_000)
            || usage.Bytes + incomingBytes > Math.Clamp(_platformOptions.ProjectDailyBytesLimit, 1_048_576, 107_374_182_400))
            throw new ConflictException("The project analysis ingestion quota for the rolling 24-hour window is exceeded.");
    }
}
