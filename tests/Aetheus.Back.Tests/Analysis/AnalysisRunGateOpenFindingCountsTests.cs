// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// Recette R-74: the Gate tab's findings figure covers every category, but a grade line counts only the findings of its
/// own domain. The gate therefore carries the open findings per category, so the tab can say how many of the total sit
/// in a domain the grade does not count.
/// </summary>
public sealed class AnalysisRunGateOpenFindingCountsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        _db.Organizations.Add(new Organization { Id = 1, Name = "Aetheus", Slug = "aetheus" });
        _db.Projects.Add(new Project { Id = 1, Name = "Project", OrganizationId = 1, DefaultBranch = "main" });
        _db.Pipelines.Add(new Pipeline { Id = 1, Name = "candidate", ProjectId = 1 });
        _db.PipelineRuns.Add(new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Success, BranchName = "main", CommitHash = new string('a', 40) });
        var next = 1;
        foreach (var (category, status) in new[]
        {
            (AnalysisCategory.Sast, AnalysisFindingStatus.Open),
            (AnalysisCategory.Secrets, AnalysisFindingStatus.Open),
            (AnalysisCategory.Architecture, AnalysisFindingStatus.Open),
            (AnalysisCategory.Architecture, AnalysisFindingStatus.Open),
            (AnalysisCategory.Architecture, AnalysisFindingStatus.Open),
            // A decided finding is not left to do: it is not among the open ones.
            (AnalysisCategory.Architecture, AnalysisFindingStatus.Accepted)
        })
        {
            var id = next++;
            var report = new AnalysisReport
            {
                Id = id,
                OrganizationId = 1,
                ProjectId = 1,
                PipelineRunId = 1,
                ScannerKey = $"scanner-{id}",
                ScannerName = $"Scanner {id}",
                ScannerVersion = "1.0.0",
                Category = category,
                Status = AnalysisReportStatus.Passed,
                Format = AnalysisReportFormat.Sarif,
                ContentHash = id.ToString("D64", System.Globalization.CultureInfo.InvariantCulture),
                StartedAt = Now.AddMinutes(-1),
                CompletedAt = Now,
                CreatedAt = Now
            };
            var finding = new AnalysisFinding
            {
                Id = id,
                OrganizationId = 1,
                ProjectId = 1,
                Fingerprint = $"finding-{id}",
                RuleId = $"rule-{id}",
                Category = category,
                Severity = AnalysisSeverity.Medium,
                Confidence = AnalysisConfidence.High,
                Status = status,
                Title = $"Finding {id}",
                Message = "m",
                FirstSeenAt = Now,
                LastSeenAt = Now,
                CreatedAt = Now,
                UpdatedAt = Now
            };
            _db.AnalysisReports.Add(report);
            _db.AnalysisFindings.Add(finding);
            _db.AnalysisFindingOccurrences.Add(new AnalysisFindingOccurrence
            {
                Id = id,
                AnalysisReport = report,
                AnalysisFinding = finding,
                LocationHash = $"location-{id}",
                ToolName = report.ScannerName,
                ScannerKey = report.ScannerKey,
                RuleId = finding.RuleId,
                FilePath = "src/A.cs",
                StartLine = 1,
                Message = "m",
                IsNew = true,
                CreatedAt = Now
            });
        }
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static void AssertCounts(AnalysisRunGateDto gate)
    {
        Assert.Equal(5, gate.FindingCount);
        Assert.Equal(1, gate.OpenFindingCounts.Single(count => count.Category == AnalysisCategory.Sast).Count);
        Assert.Equal(1, gate.OpenFindingCounts.Single(count => count.Category == AnalysisCategory.Secrets).Count);
        Assert.Equal(3, gate.OpenFindingCounts.Single(count => count.Category == AnalysisCategory.Architecture).Count);
        Assert.Equal(gate.FindingCount, gate.OpenFindingCounts.Sum(count => count.Count));
    }

    [Fact]
    public async Task TheGateCarriesTheOpenFindingsPerCategory()
    {
        await SeedAsync();

        AssertCounts(await new AnalysisRepository(_db).GetRunGateAsync(1, TestContext.Current.CancellationToken));
    }

    // The run page merges a tree of runs and takes its figures from the findings page (seen on the local instance:
    // the merged gate lost the per-category counts, so the tile never said what it does not count).
    [Fact]
    public async Task TheFindingsPageCarriesThemForTheWholeSet()
    {
        await SeedAsync();

        var page = await new AnalysisRunResultRepository(_db).GetFindingsPageAsync(
            [1], new AnalysisRunFindingsRequest { RunIds = [1] }, 1, 1, TestContext.Current.CancellationToken);

        Assert.Equal(5, page.OpenCount);
        Assert.Equal(3, page.OpenCountsByCategory.Single(count => count.Category == AnalysisCategory.Architecture).Count);
        Assert.Equal(page.OpenCount, page.OpenCountsByCategory.Sum(count => count.Count));
    }

    // The run page reads the summary, whose findings are limited and whose counts come from the database.
    [Fact]
    public async Task TheRunSummaryCarriesThemToo()
    {
        await SeedAsync();

        AssertCounts(await new AnalysisRepository(_db).GetRunResultSummaryAsync(
            1, AnalysisRunGateDto.SummaryFindingLimit, TestContext.Current.CancellationToken));
    }
}
