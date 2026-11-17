// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Reads one report of the CVE watch as a run (R-22): what it lists and what is new in it.</summary>
internal sealed class CveWatchRunRepository(AppDbContext db)
{
    /// <summary>The findings listed past this many are counted, not returned.</summary>
    internal const int MaxFindings = 500;

    /// <summary>
    /// The run whose number is <paramref name="reportId"/>, when it is a report of the watch (not a pipeline's) of a
    /// project in <paramref name="organizationIds"/> (null for a site Admin) and <paramref name="projectIds"/>
    /// (the caller's read scope, null for everything). Any other report is not found.
    /// </summary>
    public async Task<CveWatchRunDto?> GetAsync(
        int reportId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? projectIds, CancellationToken ct)
    {
        var reports = db.AnalysisReports.AsNoTracking()
            .Where(report => report.Id == reportId && report.ScannerKey == CveWatchFindings.ScannerKey && report.PipelineRunId == null);
        if (organizationIds is not null)
        {
            var organizations = organizationIds.ToArray();
            reports = reports.Where(report => organizations.Contains(report.OrganizationId));
        }

        if (projectIds is not null)
        {
            var readable = projectIds.ToArray();
            reports = reports.Where(report => readable.Contains(report.ProjectId));
        }

        var head = await reports.Select(report => new
        {
            report.Id,
            report.OrganizationId,
            report.ProjectId,
            ProjectName = report.Project.Name,
            report.Category,
            report.CompletedAt
        }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (head is null) return null;

        var occurrences = await db.AnalysisFindingOccurrences.AsNoTracking()
            .Where(occurrence => occurrence.AnalysisReportId == reportId)
            .Select(occurrence => new
            {
                occurrence.AnalysisFindingId,
                occurrence.AnalysisFinding.Title,
                occurrence.AnalysisFinding.Severity,
                occurrence.AnalysisFinding.Status,
                occurrence.IsNew,
                occurrence.Symbol
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var findings = occurrences
            .GroupBy(occurrence => occurrence.AnalysisFindingId)
            .Select(group => new CveWatchRunFindingDto
            {
                FindingId = group.Key,
                Title = group.First().Title,
                Severity = group.First().Severity,
                Status = group.First().Status,
                IsNew = group.Any(occurrence => occurrence.IsNew),
                Packages = [.. group.Select(occurrence => occurrence.Symbol).Where(symbol => !string.IsNullOrEmpty(symbol))
                    .Select(symbol => symbol!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            })
            .OrderByDescending(finding => finding.IsNew).ThenByDescending(finding => finding.Severity).ThenBy(finding => finding.FindingId)
            .ToList();
        return new CveWatchRunDto
        {
            ReportId = head.Id,
            OrganizationId = head.OrganizationId,
            ProjectId = head.ProjectId,
            ProjectName = head.ProjectName,
            Category = head.Category,
            WrittenAt = head.CompletedAt,
            FindingCount = findings.Count,
            NewFindingCount = findings.Count(finding => finding.IsNew),
            PackageCount = occurrences.Select(occurrence => occurrence.Symbol).Where(symbol => !string.IsNullOrEmpty(symbol))
                .Distinct(StringComparer.Ordinal).Count(),
            Findings = [.. findings.Take(MaxFindings)]
        };
    }
}
