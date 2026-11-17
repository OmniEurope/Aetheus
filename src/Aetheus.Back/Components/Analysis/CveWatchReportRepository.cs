// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis;

/// <summary>An open match with what the advisory says about it, as the finding of the watch is built from it.</summary>
internal sealed record CveWatchMatchRow(
    long MatchId,
    int OrganizationId,
    string Ecosystem,
    string PackageName,
    string Version,
    VulnerabilityMatchStatus Status,
    string? FixedVersion,
    string ExternalId,
    string? PrimaryCveId,
    AnalysisSeverity Severity,
    string? Summary);

/// <summary>
/// Reads the matches of the vulnerability watch and the findings they became. It reads the shared entities
/// itself, as Analysis sits above the watch: the watch never calls up into it.
/// </summary>
internal sealed class CveWatchReportRepository(AppDbContext db)
{
    /// <summary>
    /// The projects, after <paramref name="afterProjectId"/>, that may have something to publish: an open match
    /// with no finding, a finding that was fixed, or one whose confidence or severity no longer says what the match
    /// says; a closed match whose finding is still active. It is a cheap first cut, the exact decision is the
    /// publisher's (two matches may stand for one finding).
    /// </summary>
    public async Task<List<int>> GetCandidateProjectIdsAsync(int afterProjectId, int take, CancellationToken ct)
    {
        var matches = db.VulnerabilityMatches.AsNoTracking().Where(match => match.ProjectId > afterProjectId);
        var unpublished = matches.Where(match => match.ClosedAt == null
            && (match.AnalysisFindingId == null
                || match.AnalysisFinding!.Status == AnalysisFindingStatus.Fixed
                || (match.Status == VulnerabilityMatchStatus.Affected && match.AnalysisFinding.Confidence != AnalysisConfidence.High)
                || (match.Status == VulnerabilityMatchStatus.ToVerify && match.AnalysisFinding.Confidence != AnalysisConfidence.Low)
                || match.AnalysisFinding.Severity < match.Advisory.Severity))
            .Select(match => match.ProjectId);
        var unclosed = matches.Where(match => match.ClosedAt != null && match.AnalysisFindingId != null
                && match.AnalysisFinding!.Status != AnalysisFindingStatus.Fixed)
            .Select(match => match.ProjectId);
        return await unpublished.Union(unclosed).OrderBy(projectId => projectId).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Forgets what a failed publication left tracked, so the next project does not save it.</summary>
    public void DiscardUnsavedChanges() => db.ChangeTracker.Clear();

    public async Task<List<CveWatchMatchRow>> GetOpenMatchesAsync(int projectId, CancellationToken ct) =>
        await db.VulnerabilityMatches.AsNoTracking()
            .Where(match => match.ProjectId == projectId && match.ClosedAt == null && match.ProjectInventoryItemId != null)
            .OrderBy(match => match.Id)
            .Select(match => new CveWatchMatchRow(
                match.Id, match.OrganizationId, match.Ecosystem, match.PackageName, match.Version, match.Status, match.FixedVersion,
                match.Advisory.ExternalId, match.Advisory.PrimaryCveId, match.Advisory.Severity, match.Advisory.Summary))
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>The fingerprints of the findings the watch published in this category that are not fixed.</summary>
    public async Task<HashSet<string>> GetActiveFingerprintsAsync(int projectId, AnalysisCategory category, CancellationToken ct) =>
        (await db.AnalysisFindings.AsNoTracking()
            .Where(finding => finding.ProjectId == projectId
                && finding.Category == category
                && finding.Status != AnalysisFindingStatus.Fixed
                && finding.Occurrences.Any(occurrence => occurrence.ScannerKey == CveWatchFindings.ScannerKey && occurrence.BranchName == null))
            .Select(finding => finding.Fingerprint)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Points each match at its finding. Only the matches of this project are touched.</summary>
    public async Task LinkMatchesAsync(int projectId, IReadOnlyDictionary<long, int> findingByMatch, CancellationToken ct)
    {
        if (findingByMatch.Count == 0) return;
        var ids = findingByMatch.Keys.ToList();
        var matches = await db.VulnerabilityMatches
            .Where(match => match.ProjectId == projectId && ids.Contains(match.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var match in matches) match.AnalysisFindingId = findingByMatch[match.Id];
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var match in matches) db.Entry(match).State = EntityState.Detached;
    }
}
