// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

[Collection(RemoteVirtualizedGridCollection.Name)]
public sealed class ProjectQualityVirtualizedGridTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectQualityVirtualizedGridTests() => _handler = BunitTestHelper.RegisterServices(this);

    /// <summary>The browser's grid is virtualized: its first block is not the section's page of 25. Whatever the block, the
    /// Fixed tab's first read is one request.</summary>
    [Fact]
    public void FixedTab_WithTheVirtualizedGrid_ReadsItsFindingsOnce()
    {
        AetheusGrid.RemoteVirtualization = true;
        try
        {
            _handler.SetJsonResponse("api/pipelines/projects/15/quality-trend", new ProjectQualityTrendDto());
            MockAnalysisEndpoints(15, new AnalysisProjectSummaryDto { ProjectId = 15, FixedCount = 1 });
            _handler.SetJsonResponse("api/analysis/projects/15/findings?page=1&pageSize=25&status=Fixed",
                new PaginatedResult<AnalysisFindingDto> { Items = [Finding(11, "Fixed virtual") with { Status = AnalysisFindingStatus.Fixed }], TotalCount = 1, Page = 1, PageSize = 25 });
            var navigation = Services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo(navigation.GetUriWithQueryParameter("tab", "fixed"));

            var cut = Render<ProjectQualitySection>(parameters => parameters.Add(component => component.ProjectId, 15));

            cut.WaitForState(() => FindingsReads(15) >= 1);
            cut.WaitForAssertion(() => Assert.Equal(1, FindingsReads(15)), TimeSpan.FromSeconds(30));
        }
        finally { AetheusGrid.RemoteVirtualization = false; }
    }

    private int FindingsReads(int projectId) => _handler.Requests.Count(request =>
        request.Url.Contains($"api/analysis/projects/{projectId}/findings", StringComparison.Ordinal));

    private static AnalysisFindingDto Finding(int id, string title) => new()
    {
        Id = id,
        ProjectId = 6,
        RuleId = $"rule-{id}",
        Title = title,
        Message = $"Evidence {id}",
        Category = AnalysisCategory.CodeQuality,
        Severity = AnalysisSeverity.Low,
        Status = AnalysisFindingStatus.Open
    };

    private void MockAnalysisEndpoints(int projectId, AnalysisProjectSummaryDto? summary = null)
    {
        var prefix = $"api/analysis/projects/{projectId}";
        _handler.SetJsonResponse($"{prefix}/summary", summary ?? new AnalysisProjectSummaryDto());
        _handler.SetJsonResponse($"{prefix}/findings?page=1&pageSize=25&status=Open", new PaginatedResult<AnalysisFindingDto>());
    }
}
