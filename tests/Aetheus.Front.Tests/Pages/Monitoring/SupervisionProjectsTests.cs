// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Monitoring;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Monitoring;

/// <summary> R1-67 and R1-69: the Supervision is one grid of projects with their applications and their
/// storage, and it follows the changes without a manual reload. </summary>
public sealed class SupervisionProjectsTests : BunitContext
{
    private const long Megabyte = 1024 * 1024;
    private readonly BunitTestHelper.TestHandler _handler;

    public SupervisionProjectsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ProjectStorageReportDto StorageOf(params ProjectStorageUsageDto[] projects) => new() { Projects = [.. projects] };

    private static ProjectStorageUsageDto Usage(int id, string name, int usedMegabytes, int percent) => new()
    {
        ProjectId = id,
        ProjectName = name,
        BudgetBytes = 200 * Megabyte,
        UsedBytes = usedMegabytes * Megabyte,
        UsedPercent = percent,
        Tables = [new ProjectStorageTableDto("AppMetricSamples", 1000, usedMegabytes * Megabyte)]
    };

    private static MonitoredAppStatusDto App(int id, string name, int projectId, string project, AppHealthStatus status) =>
        new() { Id = id, Name = name, ProjectId = projectId, ProjectName = project, CurrentStatus = status };

    private void Summary(params MonitoredAppStatusDto[] applications) =>
        _handler.SetJsonResponse("api/appmonitoring/summary", new AppMonitoringSummaryDto { Applications = [.. applications] });

    [Fact]
    public void R167_OneGrid_ListsEveryProject_ItsApplicationsGroupedUnderIt_AndItsStorageOnce()
    {
        _handler.SetJsonResponse("api/data-storage/projects", StorageOf(
            Usage(1, "Aetheus", 170, 85), Usage(2, "Orpheus", 10, 5), Usage(3, "Sandbox", 1, 0)));
        Summary(
            App(3, "Aetheus prod", 1, "Aetheus", AppHealthStatus.Up),
            App(4, "Aetheus QA", 1, "Aetheus", AppHealthStatus.Down),
            App(8, "Orpheus web", 2, "Orpheus", AppHealthStatus.Up));

        var cut = Render<Supervision>();

        cut.WaitForAssertion(() => Assert.Contains("Sandbox", cut.Markup, StringComparison.Ordinal));
        // A project the caller can read appears even without a supervised application, said "not monitored".
        Assert.Contains("ProjectUnmanaged", cut.Markup, StringComparison.Ordinal);
        // The QA that is Down sits under its own project, next to the prod that is Up.
        Assert.Contains("Aetheus (2)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Aetheus QA", cut.Markup, StringComparison.Ordinal);
        // Three projects, four rows: the usage and the budget are shown once per project.
        Assert.Equal(3, cut.FindAll("[data-testid=project-storage-used]").Count);
        Assert.Equal(3, cut.FindAll("[data-testid=project-storage-share]").Count);
        Assert.Contains("85 %", cut.Markup, StringComparison.Ordinal);
        // One grid only; the redundant "Since" column and the "Largest table" column are gone.
        Assert.Single(cut.FindAll(".supervision-projects-grid"));
        Assert.DoesNotContain(">Since<", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectStorageLargestTable", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R167_TheRows_AreOnePerApplication_OrOnePerProjectWithout_AndTheStorageRidesOnTheFirstOnly()
    {
        var rows = SupervisionRow.Build(
            [
                App(4, "QA", 1, "Aetheus", AppHealthStatus.Down),
                App(3, "Prod", 1, "Aetheus", AppHealthStatus.Up)
            ],
            StorageOf(Usage(1, "Aetheus", 170, 85), Usage(3, "Sandbox", 1, 0)));

        Assert.Equal(["app-3", "app-4", "project-3"], rows.Select(row => row.Key));
        Assert.Equal([true, false, true], rows.Select(row => row.ShowsStorage));
        Assert.Equal(170 * Megabyte, rows[0].UsedBytes);
        Assert.Null(rows[1].UsedBytes);
        Assert.Equal(SupervisionRow.Unmanaged, rows[2].StatusKey);
        Assert.Equal("/projects/3/monitoring", rows[2].Href);
        Assert.Equal("/projects/1/monitoring?app=3", rows[0].Href);
    }

    [Fact]
    public void R167_AProjectOnlyKnownByItsApplication_StillAppears_WhenTheStorageIsMissing()
    {
        var rows = SupervisionRow.Build([App(3, "Prod", 1, "Aetheus", AppHealthStatus.Up)], null);

        var row = Assert.Single(rows);
        Assert.Equal("Aetheus", row.ProjectName);
        Assert.Null(row.UsedBytes);
    }

    [Fact]
    public void R167_TheReclaimLink_StaysAboveTheGrid_ForAnAdministratorOnly()
    {
        var admin = Render<SupervisionProjectsGrid>(parameters => parameters.Add(grid => grid.ShowReclaimLink, true));
        Assert.Equal("#reclaim-space", admin.Find("[data-testid=reclaim-space-link]").GetAttribute("href"));

        var other = Render<SupervisionProjectsGrid>();
        Assert.Empty(other.FindAll("[data-testid=reclaim-space-link]"));
    }

    [Fact]
    public void R167_AFailedStorageRead_KeepsTheApplications_AndSaysTheStorageIsMissing()
    {
        _handler.SetResponse(HttpMethod.Get, "api/data-storage/projects", HttpStatusCode.InternalServerError);
        Summary(App(3, "Aetheus prod", 1, "Aetheus", AppHealthStatus.Up));

        var cut = Render<Supervision>();

        cut.WaitForAssertion(() => Assert.Contains("Aetheus prod", cut.Markup, StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll("[data-testid=supervision-storage-failed]"));
    }

    [Theory]
    [InlineData(5, OmniTone.Neutral)]
    [InlineData(80, OmniTone.Warning)]
    [InlineData(100, OmniTone.Danger)]
    public void TheShare_TurnsOrangeAt80PercentAndRedAtTheBudget(int percent, OmniTone expected)
    {
        Assert.Equal(expected, SupervisionProjectsGrid.ShareTone(percent));
    }

    [Fact]
    public async Task R169_AReload_ShowsTheNewApplicationAndStatus_WithoutLeavingThePage()
    {
        _handler.SetJsonResponse("api/data-storage/projects", StorageOf());
        Summary(App(3, "Aetheus prod", 1, "Aetheus", AppHealthStatus.Up));
        var cut = Render<Supervision>();
        cut.WaitForAssertion(() => Assert.Contains("Aetheus prod", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Orpheus web", cut.Markup, StringComparison.Ordinal);

        Summary(App(3, "Aetheus prod", 1, "Aetheus", AppHealthStatus.Down), App(8, "Orpheus web", 4, "Orpheus", AppHealthStatus.Up));
        await cut.InvokeAsync(() => cut.Instance.ReloadAsync());

        cut.WaitForAssertion(() => Assert.Contains("Orpheus web", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("AppHealthDown", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R169_AProjectChangePush_ReloadsThePage_AndAFailedReloadKeepsTheRows()
    {
        _handler.SetJsonResponse("api/data-storage/projects", StorageOf());
        Summary(App(3, "Aetheus prod", 1, "Aetheus", AppHealthStatus.Up));
        var cut = Render<Supervision>();
        cut.WaitForAssertion(() => Assert.Contains("Aetheus prod", cut.Markup, StringComparison.Ordinal));
        Summary(App(9, "Fresh app", 5, "Fresh", AppHealthStatus.Up));

        // Only a Project change reloads the page; the burst is coalesced into one trailing reload.
        await cut.InvokeAsync(() => cut.Instance.OnEntityChangedAsync(ResourceType.Server));
        await cut.InvokeAsync(() => cut.Instance.OnEntityChangedAsync(ResourceType.Project));

        cut.WaitForAssertion(() => Assert.Contains("Fresh app", cut.Markup, StringComparison.Ordinal));

        _handler.SetResponse(HttpMethod.Get, "api/appmonitoring/summary", HttpStatusCode.InternalServerError);
        await cut.InvokeAsync(() => cut.Instance.ReloadAsync());
        Assert.Contains("Fresh app", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("GridLoadFailed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R169_ThePageAlsoReloadsOnATimer_ForTheFiguresNoPushAnnounces()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Supervision.FallbackRefreshInterval);
    }
}
