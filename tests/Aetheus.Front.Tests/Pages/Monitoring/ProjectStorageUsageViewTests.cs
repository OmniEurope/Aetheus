// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Monitoring;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Monitoring;

/// <summary>PLAN-013 lot 5: the Supervision lists each project's storage against its budget.</summary>
public sealed class ProjectStorageUsageViewTests : BunitContext
{
    private const long Megabyte = 1024 * 1024;
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectStorageUsageViewTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void EachProject_ShowsItsUsageBudgetAndShare()
    {
        _handler.SetJsonResponse("api/data-storage/projects", new ProjectStorageReportDto
        {
            Projects =
            [
                new ProjectStorageUsageDto
                {
                    ProjectId = 1, ProjectName = "Atlas", BudgetBytes = 200 * Megabyte, UsedBytes = 170 * Megabyte, UsedPercent = 85,
                    Tables = [new ProjectStorageTableDto("AppMetricSamples", 371_000, 170 * Megabyte)]
                },
                new ProjectStorageUsageDto { ProjectId = 2, ProjectName = "Aetheus", BudgetBytes = 200 * Megabyte, UsedBytes = 10 * Megabyte, UsedPercent = 5 }
            ]
        });

        var cut = Render<ProjectStorageUsageView>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=project-storage-share]").Count));
        Assert.Contains("Atlas", cut.Markup);
        Assert.Contains("AppMetricSamples", cut.Markup);
        Assert.Contains("85 %", cut.Markup);
        // No recent measurement of the Aetheus database: the note says the bytes per row are estimates.
        Assert.Contains("ProjectStorageEstimatedNote", cut.Find("[data-testid=project-storage-note]").TextContent);
    }

    [Theory]
    [InlineData(5, OmniTone.Neutral)]
    [InlineData(80, OmniTone.Warning)]
    [InlineData(100, OmniTone.Danger)]
    public void TheShare_TurnsOrangeAt80PercentAndRedAtTheBudget(int percent, OmniTone expected)
    {
        Assert.Equal(expected, ProjectStorageUsageView.ShareTone(percent));
    }
}
