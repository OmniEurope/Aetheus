// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Page-fed grids retain headers and existing rows beneath OE's loading bar; unloaded
/// grids must not announce an empty result.</summary>
public sealed class PageFedGridLoadingBarTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar--active";

    public PageFedGridLoadingBarTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void PipelineRunsGrid_PageLoad_ShowsTheBarOverTheRowsItHolds()
    {
        var runs = new List<PipelineRunDto>
        {
            new() { Id = 10, PipelineId = 1, PipelineName = "ci", ProjectId = 1, Status = PipelineStatus.Success }
        };
        var cut = Render<PipelineRunsGrid>(parameters => parameters.Add(p => p.Items, runs));
        Assert.Empty(cut.FindAll(Bar));

        cut.Render(parameters => parameters.Add(p => p.Items, runs).Add(p => p.IsLoading, true));

        Assert.True(cut.FindComponent<OmniDataGrid<PipelineRunTableItem>>().Instance.Busy);
        Assert.Single(cut.FindAll(Bar));
        Assert.Contains("#10", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".aetheus-loader"));

        cut.Render(parameters => parameters.Add(p => p.Items, runs).Add(p => p.IsLoading, false));

        Assert.Empty(cut.FindAll(Bar));
        Assert.Contains("#10", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitoredAppsStatusGrid_FirstLoad_ShowsTheBarOverAnEmptyBody_ThenTheEmptyState()
    {
        var cut = Render<MonitoredAppsStatusGrid>(parameters => parameters.Add(p => p.IsLoading, true));

        Assert.True(cut.FindComponent<OmniDataGrid<MonitoredAppStatusDto>>().Instance.Busy);
        Assert.NotEmpty(cut.FindAll("thead th"));
        Assert.Single(cut.FindAll(Bar));
        Assert.Empty(cut.FindAll(".empty-state"));
        Assert.Empty(cut.FindAll(".aetheus-loader"));

        cut.Render(parameters => parameters
            .Add(p => p.IsLoading, false)
            .Add(p => p.Applications, new List<MonitoredAppStatusDto>()));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(Bar));
            Assert.Single(cut.FindAll(".empty-state"));
        });
    }

    [Fact]
    public void MonitoredAppsStatusGrid_Reload_KeepsTheApplicationsUnderTheBar()
    {
        var apps = new List<MonitoredAppStatusDto>
        {
            new() { Id = 1, Name = "shop", ProjectId = 1, CurrentStatus = AppHealthStatus.Up }
        };
        var cut = Render<MonitoredAppsStatusGrid>(parameters => parameters
            .Add(p => p.Applications, apps)
            .Add(p => p.IsLoading, true));

        cut.WaitForAssertion(() => Assert.Contains("shop", cut.Markup, StringComparison.Ordinal));
        Assert.Single(cut.FindAll(Bar));
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        var apps = new List<MonitoredAppStatusDto>
        {
            new() { Id = 1, Name = "shop", ProjectId = 1, CurrentStatus = AppHealthStatus.Up }
        };
        var cut = Render<MonitoredAppsStatusGrid>(parameters => parameters.Add(p => p.Applications, apps));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 1);
    }
}
