// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R1-32: the overview is split in tabs kept in the URL; the essentials come first and the rest is
/// shared out by theme, each tab building its content when it is shown.</summary>
public class ServerOverviewTabsTests : BunitContext
{
    public ServerOverviewTabsTests() => BunitTestHelper.RegisterServices(this);

    private static ServerDetailDto MakeServer() => new()
    {
        Id = 1,
        Name = "web-01",
        Status = ServerStatus.Online,
        Type = ServerType.Normal,
        RecentTasks = [new ServerTaskDto { Name = "nightly-task", Status = TaskExecutionStatus.Success, CreatedAt = DateTime.UtcNow }]
    };

    /// <summary>Opens the overview on one of its tabs, the way the address bar does, before the section renders.</summary>
    internal static void OpenTab(BunitContext context, string? tab) =>
        context.Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo(tab is null ? "/servers/1/overview" : $"/servers/1/overview?tab={tab}");

    private IRenderedComponent<ServerOverviewSection> RenderOn(string? tab)
    {
        OpenTab(this, tab);
        return Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, MakeServer())
            .Add(x => x.MetricsReceived, true)
            .Add(x => x.MetricsLoadCompleted, true));
    }

    [Fact]
    public void TheFiveTabs_AreListedInOrder_WithTheEssentialsFirst()
    {
        var cut = RenderOn(null);

        var tabs = cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()).ToList();

        Assert.Equal(["OverviewTabEssential", "OverviewTabStorage", "OverviewTabRunner", "OverviewTabSsh", "OverviewTabTasks"], tabs);
        Assert.Equal(["essential", "storage", "runner", "ssh", "tasks"], ServerOverviewSection.TabSlugs);
    }

    [Fact]
    public void WithoutATabInTheUrl_TheEssentialsShow_AndTheOtherThemesDoNot()
    {
        var cut = RenderOn(null);

        // Essential information, heartbeat, CPU, memory, disk and the Services tile.
        Assert.Contains("essentials-grid", cut.Markup);
        Assert.Equal(5, cut.FindAll(".metric-tile-grid > *").Count);
        Assert.NotEmpty(cut.FindAll("a.omni-card--action[href=\"/servers/1/services\"]"));
        // Nothing of the other tabs is built yet.
        Assert.Empty(cut.FindAll(".storage-overview-card"));
        Assert.Empty(cut.FindAll(".overview-feature-card"));
        Assert.Empty(cut.FindAll(".ssh-config-card"));
        Assert.DoesNotContain("nightly-task", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageTab_ShowsTheStorageDiagnostic_Only()
    {
        var cut = RenderOn("storage");

        Assert.Single(cut.FindAll(".storage-overview-card"));
        Assert.Empty(cut.FindAll(".overview-feature-card"));
    }

    [Fact]
    public void RunnerTab_ShowsTheRunnerTheIsolationAndTheAnalysisCapabilities()
    {
        var cut = RenderOn("runner");

        // The pipeline runner (with its toolchain and deploy target) and the container isolation tiles.
        Assert.Equal(2, cut.FindAll(".overview-feature-card").Count);
        Assert.Contains("PipelineRunner", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ContainerIsolation", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("AnalysisScannerCapabilities", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".storage-overview-card"));
    }

    [Fact]
    public void SshTab_ShowsTheSshConfiguration()
    {
        var cut = RenderOn("ssh");

        Assert.Single(cut.FindAll(".ssh-config-card"));
        Assert.Empty(cut.FindAll(".storage-overview-card"));
    }

    [Fact]
    public void TasksTab_ShowsTheRecentTasks()
    {
        var cut = RenderOn("tasks");

        Assert.Contains("nightly-task", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("RecentTasks", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".ssh-config-card"));
    }
}
