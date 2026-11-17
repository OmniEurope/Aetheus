// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Reads what the CVE watch holds for one project: the counts of its open CVE, the inventories that feed the watch
/// and the date of its last report. The caller's reach is part of the query, as on the other watch reads.</summary>
internal sealed class CveWatchProjectRepository(AppDbContext db)
{
    /// <summary>The project, null when it does not exist or the caller may not read it (organization and project scope both apply).</summary>
    public async Task<CveWatchProjectDto?> GetAsync(int projectId, CveWatchScope scope, CancellationToken ct)
    {
        var projects = db.Projects.AsNoTracking().Where(project => project.Id == projectId);
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

        var organizationId = await projects.Select(project => (int?)project.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (organizationId is null) return null;

        var open = db.VulnerabilityMatches.AsNoTracking()
            .Where(match => match.ProjectId == projectId && match.OrganizationId == organizationId && match.ClosedAt == null);
        var advisories = await open
            .Select(match => new { Id = match.Advisory.ExternalId, match.Advisory.Severity })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var exploited = await open.Where(match => match.Advisory.KevAddedAt != null)
            .Select(match => match.Advisory.ExternalId).Distinct().CountAsync(ct).ConfigureAwait(false);
        var withoutFix = await open.Where(match => match.FixedVersion == null)
            .Select(match => match.Advisory.ExternalId).Distinct().CountAsync(ct).ConfigureAwait(false);
        var inventories = await db.ProjectInventories.AsNoTracking()
            .Where(inventory => inventory.ProjectId == projectId && inventory.OrganizationId == organizationId)
            .OrderBy(inventory => inventory.Origin).ThenBy(inventory => inventory.GitInternalRepo!.Name)
            .Select(inventory => new CveWatchProjectInventoryDto
            {
                Origin = inventory.Origin,
                RepositoryName = inventory.GitInternalRepo == null ? null : inventory.GitInternalRepo.Name,
                Ref = inventory.Ref,
                ReadAt = inventory.ReadAt,
                Coverage = inventory.Coverage,
                Reason = inventory.Reason,
                ItemCount = inventory.ItemCount
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var lastReport = await db.AnalysisReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId && report.OrganizationId == organizationId
                && report.ScannerKey == CveWatchFindings.ScannerKey && report.PipelineRunId == null)
            .OrderByDescending(report => report.CompletedAt).ThenByDescending(report => report.Id)
            .Select(report => new { report.Id, report.CompletedAt })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        int Count(AnalysisSeverity severity) => advisories.Count(item => item.Severity == severity);
        return new CveWatchProjectDto
        {
            ProjectId = projectId,
            OpenCount = advisories.Count,
            CriticalCount = Count(AnalysisSeverity.Critical),
            HighCount = Count(AnalysisSeverity.High),
            MediumCount = Count(AnalysisSeverity.Medium),
            LowCount = Count(AnalysisSeverity.Low),
            ExploitedCount = exploited,
            WithoutFixCount = withoutFix,
            LastWatchedAt = lastReport?.CompletedAt,
            LastWatchRunId = lastReport?.Id,
            Inventories = inventories
        };
    }
}
