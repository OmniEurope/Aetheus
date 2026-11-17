// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Reads the watch's matches for the API, as Analysis sits above the watch.</summary>
internal sealed class CveWatchQueryRepository(AppDbContext db)
{
    internal static readonly System.Linq.Expressions.Expression<Func<VulnerabilityMatch, CveWatchMatchDto>> ToDto =
        match => new CveWatchMatchDto
        {
            MatchId = match.Id,
            OrganizationId = match.OrganizationId,
            ProjectId = match.ProjectId,
            ProjectName = match.Project.Name,
            AdvisoryId = match.Advisory.ExternalId,
            CveId = match.Advisory.PrimaryCveId,
            Summary = match.Advisory.Summary,
            Severity = match.Advisory.Severity,
            SeverityPublished = match.Advisory.SeverityPublished,
            ExploitedSince = match.Advisory.KevAddedAt,
            Ecosystem = match.Ecosystem,
            PackageName = match.PackageName,
            Version = match.Version,
            FixedVersion = match.FixedVersion,
            Status = match.Status,
            FirstMatchedAt = match.FirstMatchedAt,
            ClosedAt = match.ClosedAt,
            NotifiedAt = match.NotifiedAt,
            FindingId = match.AnalysisFindingId,
            FindingStatus = match.AnalysisFinding == null ? null : match.AnalysisFinding.Status,
            InventoryOrigin = match.ProjectInventoryItem == null ? null : match.ProjectInventoryItem.ProjectInventory.Origin,
            InventoryRef = match.ProjectInventoryItem == null ? null : match.ProjectInventoryItem.ProjectInventory.Ref
        };

    public async Task<PaginatedResult<CveWatchMatchDto>> GetPageAsync(
        int organizationId, IReadOnlyCollection<int>? accessibleProjectIds, int? projectId, bool includeClosed,
        int page, int pageSize, CancellationToken ct)
    {
        // The organization is part of the row, not only of the caller's grant: a project id of another
        // organization in the grant, or in the query, finds nothing here.
        var query = db.VulnerabilityMatches.AsNoTracking().Where(match => match.OrganizationId == organizationId);
        if (accessibleProjectIds is not null)
        {
            var readable = accessibleProjectIds.ToArray();
            query = query.Where(match => readable.Contains(match.ProjectId));
        }

        if (projectId is { } only) query = query.Where(match => match.ProjectId == only);
        if (!includeClosed) query = query.Where(match => match.ClosedAt == null);
        var items = await query
            .OrderByDescending(match => match.Advisory.KevAddedAt != null)
            .ThenByDescending(match => match.Advisory.Severity)
            .ThenByDescending(match => match.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToDto)
            .ToListAsync(ct).ConfigureAwait(false);
        // A page shorter than its size is the last one and already holds the total: no second scan of the matches.
        var total = await query.CountGivenPageAsync(page, pageSize, items.Count, ct).ConfigureAwait(false);
        return new PaginatedResult<CveWatchMatchDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    /// <summary>
    /// One match, when the caller may read it: <paramref name="organizationIds"/> is null for a site Admin, otherwise
    /// the organizations the caller belongs to, and <paramref name="accessibleProjectIds"/> is the read scope as on
    /// the list. A match outside both is not found, as if it did not exist.
    /// </summary>
    public async Task<CveWatchMatchDto?> GetOneAsync(
        long matchId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? accessibleProjectIds, CancellationToken ct)
    {
        var query = db.VulnerabilityMatches.AsNoTracking().Where(match => match.Id == matchId);
        if (organizationIds is not null)
        {
            var organizations = organizationIds.ToArray();
            query = query.Where(match => organizations.Contains(match.OrganizationId));
        }

        if (accessibleProjectIds is not null)
        {
            var readable = accessibleProjectIds.ToArray();
            query = query.Where(match => readable.Contains(match.ProjectId));
        }

        return await query.Select(ToDto).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The match an analysis finding was written for (the open one first, else the latest), with the reach rules of <see cref="GetOneAsync"/>.</summary>
    public async Task<CveWatchMatchDto?> GetOneByFindingAsync(
        int findingId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? accessibleProjectIds, CancellationToken ct)
    {
        var query = db.VulnerabilityMatches.AsNoTracking().Where(match => match.AnalysisFindingId == findingId);
        if (organizationIds is not null)
        {
            var organizations = organizationIds.ToArray();
            query = query.Where(match => organizations.Contains(match.OrganizationId));
        }

        if (accessibleProjectIds is not null)
        {
            var readable = accessibleProjectIds.ToArray();
            query = query.Where(match => readable.Contains(match.ProjectId));
        }

        return await query
            .OrderByDescending(match => match.ClosedAt == null).ThenByDescending(match => match.Id)
            .Select(ToDto).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
