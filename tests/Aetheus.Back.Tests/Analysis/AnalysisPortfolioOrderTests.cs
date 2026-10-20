// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Each sortable portfolio column orders both ways; another grid column sorts through the
/// column map, and anything else falls back to the newest first.</summary>
public sealed class AnalysisPortfolioOrderTests
{
    private static readonly AnalysisReport A = Report(1, "alpha", "zeta", null, AnalysisCategory.Sast, "trivy", day: 3);
    private static readonly AnalysisReport B = Report(2, "beta", "eta", "ci", AnalysisCategory.Dast, "zap", day: 1);
    private static readonly AnalysisReport C = Report(3, "gamma", "alpha", "deploy", AnalysisCategory.Coverage, "coverlet", day: 2);

    [Theory]
    [InlineData("OrganizationName", false, new[] { 1, 2, 3 })]
    [InlineData("OrganizationName", true, new[] { 3, 2, 1 })]
    [InlineData("ProjectName", false, new[] { 3, 2, 1 })]
    [InlineData("ProjectName", true, new[] { 1, 2, 3 })]
    [InlineData("PipelineName", false, new[] { 1, 2, 3 })]
    [InlineData("PipelineName", true, new[] { 3, 2, 1 })]
    [InlineData("Category", false, new[] { 1, 2, 3 })]
    [InlineData("Category", true, new[] { 3, 2, 1 })]
    [InlineData("Status", false, new[] { 1, 2, 3 })]
    [InlineData("Status", true, new[] { 3, 2, 1 })]
    [InlineData("CompletedAt", false, new[] { 2, 3, 1 })]
    [InlineData("CompletedAt", true, new[] { 1, 3, 2 })]
    [InlineData("scannerName", false, new[] { 3, 1, 2 })]
    [InlineData("scannerName", true, new[] { 2, 1, 3 })]
    [InlineData(null, false, new[] { 1, 3, 2 })]
    [InlineData("unknownColumn", false, new[] { 1, 3, 2 })]
    public void OrderPortfolio_SortsOnTheRequestedColumn(string? sortBy, bool descending, int[] expected)
    {
        var ordered = AnalysisPortfolioQuery.OrderPortfolio(
            new[] { B, C, A }.AsQueryable(),
            new AnalysisPortfolioPaginationRequest { SortBy = sortBy, SortDescending = descending });

        Assert.Equal(expected, ordered.Select(report => report.Id));
    }

    private static AnalysisReport Report(
        int id, string organization, string project, string? pipeline, AnalysisCategory category, string scanner, int day) => new()
        {
            Id = id,
            Organization = new Organization { Name = organization },
            Project = new Project { Name = project },
            PipelineRun = pipeline is null ? null : new PipelineRun { Pipeline = new Pipeline { Name = pipeline } },
            Category = category,
            Status = (AnalysisReportStatus)(id - 1),
            ScannerName = scanner,
            CompletedAt = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc),
        };
}
