// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis;

/// <summary>One affected package of a project that the watch announces. <see cref="IsNew"/> is true while no mail has told it.</summary>
internal sealed record CveWatchAnnouncedMatch(
    long MatchId,
    int OrganizationId,
    int ProjectId,
    string ProjectName,
    string RuleId,
    string PackageName,
    string Version,
    string? FixedVersion,
    AnalysisSeverity Severity,
    bool Exploited,
    bool IsNew);

/// <summary>
/// What the watch mails about: the open matches that are certain (affected, not "to verify"), whose finding is
/// open (a risk someone accepted, or marked a false positive, is not news), and whose advisory is worth a mail:
/// exploited (KEV), Critical, or High with a severity the source really published, or one that another advisory
/// of the same CVE published. An advisory with no severity of its own counts as High for the gate, but three
/// quarters of the feed is in that case (malicious-package and distribution notices): mailing them all would
/// drown the rest.
/// <para>Two mails use it (recette R-25, R-26). The immediate one is for a NEW severe match (exploited or
/// Critical) and never waits. The nightly one lists everything still open, the new ones marked as such.
/// <c>NotifiedAt</c> is what makes a match new: set by whichever mail tells it first, cleared when the match
/// comes back after a fix.</para>
/// </summary>
internal sealed class CveWatchAnnouncementRepository(AppDbContext db)
{
    private IQueryable<VulnerabilityMatch> Worthy() =>
        db.VulnerabilityMatches.Where(match => match.ClosedAt == null
            && match.Status == VulnerabilityMatchStatus.Affected
            && match.AnalysisFinding != null
            && match.AnalysisFinding.Status == AnalysisFindingStatus.Open
            && (match.Advisory.KevAddedAt != null
                || match.Advisory.Severity == AnalysisSeverity.Critical
                || (match.Advisory.Severity == AnalysisSeverity.High && match.Advisory.SeverityPublished)
                // The runtime feeds publish no severity (releases.json, Node's list and php.net's change log carry none), but
                // another advisory of the same CVE, not withdrawn, often does: a severity belongs to the CVE, and the feed holds it.
                || (!match.Advisory.SeverityPublished && match.Advisory.PrimaryCveId != null
                    && db.VulnerabilityAdvisories.Any(other => other.Id != match.AdvisoryId
                        && other.WithdrawnAt == null
                        && other.SeverityPublished
                        && other.Severity >= AnalysisSeverity.High
                        && (other.PrimaryCveId == match.Advisory.PrimaryCveId || other.Aliases.Contains(match.Advisory.PrimaryCveId!))))));

    /// <summary>The matches that do not wait for the night when new: exploited, or Critical (published by its own
    /// advisory or by another advisory of the same CVE). A published High waits.</summary>
    private IQueryable<VulnerabilityMatch> Severe() =>
        Worthy().Where(match => match.Advisory.KevAddedAt != null
            || match.Advisory.Severity == AnalysisSeverity.Critical
            || (!match.Advisory.SeverityPublished && match.Advisory.PrimaryCveId != null
                && db.VulnerabilityAdvisories.Any(other => other.Id != match.AdvisoryId
                    && other.WithdrawnAt == null
                    && other.SeverityPublished
                    && other.Severity == AnalysisSeverity.Critical
                    && (other.PrimaryCveId == match.Advisory.PrimaryCveId || other.Aliases.Contains(match.Advisory.PrimaryCveId!)))));

    /// <summary>Every NEW severe match, of every project: what the immediate mail of a pass tells.</summary>
    public async Task<List<CveWatchAnnouncedMatch>> GetNewSevereAsync(CancellationToken ct) =>
        await Project(Severe().AsNoTracking().Where(match => match.NotifiedAt == null)).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>The projects, after <paramref name="afterProjectId"/>, that have a match worth a mail, new or not.</summary>
    public async Task<List<int>> GetOpenProjectIdsAsync(int afterProjectId, int take, CancellationToken ct) =>
        await Worthy().AsNoTracking()
            .Where(match => match.ProjectId > afterProjectId)
            .Select(match => match.ProjectId)
            .Distinct()
            .OrderBy(projectId => projectId)
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Everything still open in one project that is worth a mail, new or not: what the nightly mail lists.</summary>
    public async Task<List<CveWatchAnnouncedMatch>> GetOpenAsync(int projectId, CancellationToken ct) =>
        await Project(Worthy().AsNoTracking().Where(match => match.ProjectId == projectId)).ToListAsync(ct).ConfigureAwait(false);

    private static IQueryable<CveWatchAnnouncedMatch> Project(IQueryable<VulnerabilityMatch> matches) =>
        matches.OrderBy(match => match.Id)
            .Select(match => new CveWatchAnnouncedMatch(
                match.Id, match.OrganizationId, match.ProjectId, match.Project.Name, match.Advisory.PrimaryCveId ?? match.Advisory.ExternalId,
                match.PackageName, match.Version, match.FixedVersion, match.Advisory.Severity, match.Advisory.KevAddedAt != null,
                match.NotifiedAt == null));

    /// <summary>
    /// Marks these matches as announced and returns the ones this call marked. A match another pass marked
    /// meanwhile is not returned, so two passes at once announce it once.
    /// </summary>
    public async Task<List<long>> ClaimAsync(IReadOnlyCollection<long> matchIds, DateTime at, CancellationToken ct)
    {
        var ids = matchIds.ToList();
        var now = ToStoredPrecision(at);
        if (await SetNotifiedAtAsync(ids, expected: null, value: now, ct).ConfigureAwait(false) == 0) return [];
        return await db.VulnerabilityMatches.AsNoTracking()
            .Where(match => ids.Contains(match.Id) && match.NotifiedAt == now)
            .Select(match => match.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Takes back a claim whose announcement failed, so the next pass makes it again.</summary>
    public async Task ReleaseAsync(IReadOnlyCollection<long> matchIds, DateTime claimedAt, CancellationToken ct) =>
        await SetNotifiedAtAsync([.. matchIds], expected: ToStoredPrecision(claimedAt), value: null, ct).ConfigureAwait(false);

    /// <summary>One statement: the rows still holding <paramref name="expected"/> take <paramref name="value"/>, so a
    /// row another pass changed meanwhile is left alone. Returns how many it changed.</summary>
    private async Task<int> SetNotifiedAtAsync(List<long> ids, DateTime? expected, DateTime? value, CancellationToken ct)
    {
        var rows = db.VulnerabilityMatches.Where(match => ids.Contains(match.Id) && match.NotifiedAt == expected);
        if (db.Database.IsRelational())
            return await rows.ExecuteUpdateAsync(update => update.SetProperty(match => match.NotifiedAt, value), ct).ConfigureAwait(false);

        // The in-memory provider has no bulk update: same change, row by row.
        var tracked = await rows.ToListAsync(ct).ConfigureAwait(false);
        foreach (var match in tracked) match.NotifiedAt = value;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var match in tracked) db.Entry(match).State = EntityState.Detached;
        return tracked.Count;
    }

    /// <summary>PostgreSQL keeps microseconds: the claim is found again by the value it was stored with.</summary>
    private static DateTime ToStoredPrecision(DateTime value) =>
        new(value.Ticks - value.Ticks % (TimeSpan.TicksPerMillisecond / 1000), DateTimeKind.Utc);
}
