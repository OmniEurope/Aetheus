// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Projects;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis;

/// <summary>What a caller may read of the watch: <c>null</c> for everything (a site Admin), otherwise the
/// organizations it belongs to and the projects it may read. Both apply, as on the match detail.</summary>
public sealed record CveWatchScope(IReadOnlyCollection<int>? OrganizationIds, IReadOnlyCollection<int>? ProjectIds);

/// <summary>The filters of the CVE list of the quality page.</summary>
public sealed record CveWatchAdvisoryFilter(
    int? OrganizationId, int? ProjectId, bool IncludeClosed, AnalysisSeverity? Severity, bool ExploitedOnly, bool WithoutFixOnly);

/// <summary>Reads the watch for the quality page across organizations: the advisories with their matches, the
/// figures of the tiles and the projects the watch cannot vouch for.</summary>
internal sealed class CveWatchOverviewRepository(AppDbContext db)
{
    private IQueryable<VulnerabilityMatch> Scoped(CveWatchScope scope)
    {
        var query = db.VulnerabilityMatches.AsNoTracking();
        if (scope.OrganizationIds is not null)
        {
            var organizations = scope.OrganizationIds.ToArray();
            query = query.Where(match => organizations.Contains(match.OrganizationId));
        }

        if (scope.ProjectIds is not null)
        {
            var projects = scope.ProjectIds.ToArray();
            query = query.Where(match => projects.Contains(match.ProjectId));
        }

        return query;
    }

    private IQueryable<VulnerabilityMatch> Filtered(CveWatchScope scope, CveWatchAdvisoryFilter filter)
    {
        var query = Scoped(scope);
        if (filter.OrganizationId is { } organizationId) query = query.Where(match => match.OrganizationId == organizationId);
        if (filter.ProjectId is { } projectId) query = query.Where(match => match.ProjectId == projectId);
        if (!filter.IncludeClosed) query = query.Where(match => match.ClosedAt == null);
        if (filter.Severity is { } severity) query = query.Where(match => match.Advisory.Severity == severity);
        if (filter.ExploitedOnly) query = query.Where(match => match.Advisory.KevAddedAt != null);
        if (filter.WithoutFixOnly) query = query.Where(match => match.FixedVersion == null);
        return query;
    }

    /// <summary>A page of advisories, the exploited first then the worst, each with all its matches that pass the filter.</summary>
    public async Task<PaginatedResult<CveWatchAdvisoryGroupDto>> GetAdvisoryPageAsync(
        CveWatchScope scope, CveWatchAdvisoryFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var query = Filtered(scope, filter);
        var total = await query.Select(match => match.Advisory.ExternalId).Distinct().CountAsync(ct).ConfigureAwait(false);
        var advisoryIds = await query
            .Select(match => new
            {
                Id = match.Advisory.ExternalId,
                Exploited = match.Advisory.KevAddedAt != null,
                match.Advisory.Severity
            })
            .Distinct()
            .OrderByDescending(item => item.Exploited)
            .ThenByDescending(item => item.Severity)
            .ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => item.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var matches = await query
            .Where(match => advisoryIds.Contains(match.Advisory.ExternalId))
            .OrderBy(match => match.Project.Name).ThenBy(match => match.PackageName).ThenBy(match => match.Id)
            .Select(CveWatchQueryRepository.ToDto)
            .ToListAsync(ct).ConfigureAwait(false);
        var byAdvisory = matches.ToLookup(match => match.AdvisoryId, StringComparer.Ordinal);
        var groups = advisoryIds.Select(id =>
        {
            var members = byAdvisory[id].ToList();
            var first = members[0];
            return new CveWatchAdvisoryGroupDto
            {
                AdvisoryId = id,
                CveId = first.CveId,
                Summary = first.Summary,
                Severity = first.Severity,
                SeverityPublished = first.SeverityPublished,
                ExploitedSince = first.ExploitedSince,
                Matches = members
            };
        }).ToList();
        return new PaginatedResult<CveWatchAdvisoryGroupDto> { Items = groups, TotalCount = total, Page = page, PageSize = pageSize };
    }

    /// <summary>The three counts of the tiles: distinct open CVE, over the projects of the scope.</summary>
    public async Task<(int Critical, int Exploited, int WithoutFix)> CountOpenAsync(CveWatchScope scope, CancellationToken ct)
    {
        var open = Scoped(scope).Where(match => match.ClosedAt == null);
        var critical = await open.Where(match => match.Advisory.Severity == AnalysisSeverity.Critical)
            .Select(match => match.Advisory.ExternalId).Distinct().CountAsync(ct).ConfigureAwait(false);
        var exploited = await open.Where(match => match.Advisory.KevAddedAt != null)
            .Select(match => match.Advisory.ExternalId).Distinct().CountAsync(ct).ConfigureAwait(false);
        var withoutFix = await open.Where(match => match.FixedVersion == null)
            .Select(match => match.Advisory.ExternalId).Distinct().CountAsync(ct).ConfigureAwait(false);
        return (critical, exploited, withoutFix);
    }

    /// <summary>The active projects of the scope whose inventories never reach a complete coverage (none read, or
    /// partial): for them, "no CVE" is no proof.</summary>
    public async Task<List<CveWatchUncoveredProjectDto>> GetUncoveredProjectsAsync(CveWatchScope scope, CancellationToken ct)
    {
        var projects = db.Projects.AsNoTracking()
            .Where(project => project.Status == ProjectStatus.Active && project.DeletionRequestedAt == null);
        if (scope.OrganizationIds is not null)
        {
            var organizations = scope.OrganizationIds.ToArray();
            projects = projects.Where(project => organizations.Contains(project.OrganizationId));
        }

        if (scope.ProjectIds is not null)
        {
            var readable = scope.ProjectIds.ToArray();
            projects = projects.Where(project => readable.Contains(project.Id));
        }

        var rows = await projects
            .Where(project => !db.ProjectInventories.Any(inventory => inventory.ProjectId == project.Id
                && inventory.Coverage == ProjectInventoryCoverage.Complete))
            .OrderBy(project => project.Name)
            .Select(project => new
            {
                project.Id,
                project.Name,
                project.OrganizationId,
                OrganizationName = project.Organization.Name,
                Best = db.ProjectInventories.Where(inventory => inventory.ProjectId == project.Id)
                    .Max(inventory => (ProjectInventoryCoverage?)inventory.Coverage)
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(row => new CveWatchUncoveredProjectDto
        {
            ProjectId = row.Id,
            ProjectName = row.Name,
            OrganizationId = row.OrganizationId,
            OrganizationName = row.OrganizationName,
            Coverage = row.Best ?? ProjectInventoryCoverage.None
        }).ToList();
    }
}
