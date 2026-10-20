// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>The Gate tab's column sort: every column in both directions, the finding id breaking
/// ties, and the default "most severe first" order when no known column is asked for.</summary>
public sealed class AnalysisRunFindingFiltersSortTests
{
    private static readonly AnalysisRunGateFindingDto[] Findings =
    [
        new()
        {
            FindingId = 1, Severity = AnalysisSeverity.Low, Category = AnalysisCategory.Secrets,
            RuleId = "R2", Title = "beta", Status = AnalysisFindingStatus.Accepted
        },
        new()
        {
            FindingId = 2, Severity = AnalysisSeverity.Critical, Category = AnalysisCategory.Sast,
            RuleId = "R1", Title = "alpha", Status = AnalysisFindingStatus.Open
        },
        new()
        {
            FindingId = 3, Severity = AnalysisSeverity.Low, Category = AnalysisCategory.Sast,
            RuleId = "R2", Title = "alpha", Status = AnalysisFindingStatus.Open
        },
    ];

    [Theory]
    [InlineData("Id", false, new[] { 1, 2, 3 })]
    [InlineData("Id", true, new[] { 3, 2, 1 })]
    [InlineData("Severity", false, new[] { 1, 3, 2 })]
    [InlineData("Severity", true, new[] { 2, 1, 3 })]
    [InlineData("Category", false, new[] { 2, 3, 1 })]
    [InlineData("Category", true, new[] { 1, 2, 3 })]
    [InlineData("RuleId", false, new[] { 2, 1, 3 })]
    [InlineData("RuleId", true, new[] { 1, 3, 2 })]
    [InlineData("Title", false, new[] { 2, 3, 1 })]
    [InlineData("Title", true, new[] { 1, 2, 3 })]
    [InlineData("Status", false, new[] { 2, 3, 1 })]
    [InlineData("Status", true, new[] { 1, 2, 3 })]
    [InlineData(null, false, new[] { 2, 3, 1 })]
    [InlineData("Unknown", true, new[] { 2, 3, 1 })]
    public void Sort_OrdersByTheRequestedColumnThenByFindingId(string? sortBy, bool descending, int[] expectedIds)
    {
        var request = new AnalysisRunFindingsRequest { RunIds = [1], SortBy = sortBy, SortDescending = descending };

        var sorted = AnalysisRunFindingFilters.Sort(Enumerable.Reverse(Findings).AsQueryable(), request);

        Assert.Equal(expectedIds, sorted.Select(finding => finding.FindingId).ToArray());
    }
}
