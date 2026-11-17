// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// One affected package of a server's host system, told in the mail of a project the server is attached to (R-41).
/// A server attached to several projects gives one row per project. <see cref="IsNew"/> is true while no mail has told it.
/// </summary>
internal sealed record CveWatchAnnouncedHostFinding(
    long FindingId,
    int OrganizationId,
    int ProjectId,
    string ProjectName,
    int ServerId,
    string ServerName,
    string RuleId,
    string PackageName,
    string Version,
    string? FixedVersion,
    AnalysisSeverity Severity,
    bool Exploited,
    bool IsNew);

/// <summary>
/// What the nightly CVE mail says of the servers' host systems (R-41): the open findings that are certain (affected, not
/// "to verify") and worth a mail by the same measure as the projects' matches (exploited, Critical, or High published by its
/// own advisory), attached to the projects their server belongs to, directly or through an environment, so the people who
/// follow a project read its servers' CVE in the mail they already get. A server attached to no project is told to nobody:
/// the CVE watch tab and the server page still show it. <c>NotifiedAt</c> makes a finding new, set by the nightly mail that
/// tells it first and kept while the finding stays, as for <see cref="VulnerabilityMatch"/>.
/// </summary>
internal sealed class CveWatchHostAnnouncementRepository(AppDbContext db)
{
    private IQueryable<ServerVulnerabilityFinding> Worthy() =>
        db.ServerVulnerabilityFindings.Where(finding => finding.Status == VulnerabilityMatchStatus.Affected
            && (finding.Advisory.KevAddedAt != null
                || finding.Advisory.Severity == AnalysisSeverity.Critical
                || (finding.Advisory.Severity == AnalysisSeverity.High && finding.Advisory.SeverityPublished)));

    /// <summary>Every open finding worth a mail, one row per project its server belongs to, new or not.</summary>
    public async Task<List<CveWatchAnnouncedHostFinding>> GetOpenAsync(CancellationToken ct)
    {
        var findings = await Worthy().AsNoTracking()
            .OrderBy(finding => finding.Id)
            .Select(finding => new
            {
                finding.Id,
                finding.ServerId,
                ServerName = finding.Server.Name,
                RuleId = finding.Advisory.PrimaryCveId ?? finding.Advisory.ExternalId,
                finding.PackageName,
                Version = finding.InstalledVersion,
                finding.FixedVersion,
                finding.Advisory.Severity,
                Exploited = finding.Advisory.KevAddedAt != null,
                IsNew = finding.NotifiedAt == null
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (findings.Count == 0) return [];

        var links = await db.ProjectLinksOfServers([.. findings.Select(finding => finding.ServerId).Distinct()])
            .ToListAsync(ct).ConfigureAwait(false);
        var projectIds = links.Select(link => link.ProjectId).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .Select(project => new { project.Id, project.Name, project.OrganizationId })
            .ToDictionaryAsync(project => project.Id, ct).ConfigureAwait(false);
        var projectsOfServer = links.ToLookup(link => link.ServerId, link => link.ProjectId);
        return [.. findings.SelectMany(finding => projectsOfServer[finding.ServerId].Where(projects.ContainsKey).Select(projectId =>
            new CveWatchAnnouncedHostFinding(
                finding.Id, projects[projectId].OrganizationId, projectId, projects[projectId].Name, finding.ServerId, finding.ServerName, finding.RuleId,
                finding.PackageName, finding.Version, finding.FixedVersion, finding.Severity, finding.Exploited, finding.IsNew)))];
    }

    /// <summary>
    /// Marks these findings as told and returns the ones this call marked. A finding another night marked meanwhile is not
    /// returned, so two runs at once tell it once.
    /// </summary>
    public async Task<List<long>> ClaimAsync(IReadOnlyCollection<long> findingIds, DateTime at, CancellationToken ct)
    {
        var ids = findingIds.ToList();
        var now = ToStoredPrecision(at);
        if (await SetNotifiedAtAsync(ids, expected: null, value: now, ct).ConfigureAwait(false) == 0) return [];
        return await db.ServerVulnerabilityFindings.AsNoTracking()
            .Where(finding => ids.Contains(finding.Id) && finding.NotifiedAt == now)
            .Select(finding => finding.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Takes back a claim whose mail failed, so the next night tells it again.</summary>
    public async Task ReleaseAsync(IReadOnlyCollection<long> findingIds, DateTime claimedAt, CancellationToken ct) =>
        await SetNotifiedAtAsync([.. findingIds], expected: ToStoredPrecision(claimedAt), value: null, ct).ConfigureAwait(false);

    private async Task<int> SetNotifiedAtAsync(List<long> ids, DateTime? expected, DateTime? value, CancellationToken ct)
    {
        var rows = db.ServerVulnerabilityFindings.Where(finding => ids.Contains(finding.Id) && finding.NotifiedAt == expected);
        if (db.Database.IsRelational())
            return await rows.ExecuteUpdateAsync(update => update.SetProperty(finding => finding.NotifiedAt, value), ct).ConfigureAwait(false);

        // The in-memory provider has no bulk update: same change, row by row.
        var tracked = await rows.ToListAsync(ct).ConfigureAwait(false);
        foreach (var finding in tracked) finding.NotifiedAt = value;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var finding in tracked) db.Entry(finding).State = EntityState.Detached;
        return tracked.Count;
    }

    /// <summary>PostgreSQL keeps microseconds: the claim is found again by the value it was stored with.</summary>
    private static DateTime ToStoredPrecision(DateTime value) =>
        new(value.Ticks - value.Ticks % (TimeSpan.TicksPerMillisecond / 1000), DateTimeKind.Utc);
}
