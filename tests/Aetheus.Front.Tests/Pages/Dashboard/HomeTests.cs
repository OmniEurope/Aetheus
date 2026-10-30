// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

public class HomeTests : BunitContext
{
    public HomeTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/appmonitoring/summary",
            new AppMonitoringSummaryDto());
        BunitTestHelper.SetOnboardingWizardResponses(_handler);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    [Fact]
    public void Renders_Loading_WhenDashboardNull()
    {
        _handler.SetJsonResponse("monitoring/dashboard", new DashboardOverviewDto
        {
            TotalServers = 5,
            OnlineServers = 3,
            OfflineServers = 2,
            PendingTasks = 1,
            Servers = [],
            RecentRuns = []
        });

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("5"));

        Assert.Contains("5", cut.Markup);
        Assert.Contains("3", cut.Markup);
        Assert.Contains("2", cut.Markup);
    }

    private static PipelineRunDto Run(int id, PipelineStatus status, int? triggers = null) => new()
    {
        Id = id,
        PipelineId = id,
        PipelineName = $"p{id}",
        Status = status,
        StartedAt = DateTime.UtcNow.AddMinutes(-id),
        Steps = triggers is { } child ? [new PipelineStepRunDto { Id = id, StageName = "s", StepName = "t", TriggeredRunId = child }] : []
    };

    [Fact]
    public void R36_RecentRunsTile_CountsOnlyRootRunsRunning_AndShowsNoSuccessOrFailedBadge()
    {
        _handler.SetJsonResponse("monitoring/dashboard", new DashboardOverviewDto
        {
            // Run 1 is running and triggered run 2, also running: a single root, so "1" and not "2".
            RecentRuns = [Run(1, PipelineStatus.Running, triggers: 2), Run(2, PipelineStatus.Running), Run(3, PipelineStatus.Success), Run(4, PipelineStatus.Failed)],
            Servers = [],
            Projects = []
        });

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var tile = cut.FindAll(".dashboard-tile").Single(t => t.TextContent.Contains("RecentRuns", StringComparison.Ordinal));
            var badges = tile.QuerySelectorAll(".status-count-badge");
            var running = Assert.Single(badges);
            Assert.Equal("1", running.QuerySelector(".status-count-badge__count")!.TextContent);
            Assert.Contains("Enum_PipelineStatus_Running", running.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void R36_RecentRunsTile_ShowsNoRunningBadge_WhenNoRootRunIsRunning()
    {
        _handler.SetJsonResponse("monitoring/dashboard", new DashboardOverviewDto
        {
            RecentRuns = [Run(3, PipelineStatus.Success), Run(4, PipelineStatus.Failed)],
            Servers = [],
            Projects = []
        });

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var tile = cut.FindAll(".dashboard-tile").Single(t => t.TextContent.Contains("RecentRuns", StringComparison.Ordinal));
            Assert.NotEmpty(tile.QuerySelectorAll(".omni-data-grid"));
            Assert.Empty(tile.QuerySelectorAll(".status-count-badge"));
        });
    }

    [Fact]
    public void R41_DashboardPanels_AreTheTilesOfOneEqualHeightOeTileGrid()
    {
        _handler.SetJsonResponse("monitoring/dashboard", new DashboardOverviewDto { RecentRuns = [], Servers = [], Projects = [] });

        var cut = Render<Home>();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".dashboard-tile").Count));
        // Same width and same height, phone included (the tiles stack and share the tallest height): the OE
        // tile grid with its default equal heights, the four panels as its direct children.
        var grid = Assert.Single(cut.FindAll(".dashboard-tile-row.omni-tile-grid"));
        Assert.DoesNotContain("omni-tile-grid--natural-heights", grid.ClassName, StringComparison.Ordinal);
        Assert.Equal(4, grid.Children.Count(child => child.ClassList.Contains("dashboard-tile")));
    }
    [Fact]
    public void R43_TheCountBadgesOfThePanels_TakeTheWidthOfTheirContent()
    {
        _handler.SetJsonResponse("monitoring/dashboard", new DashboardOverviewDto
        {
            OnlineServers = 2,
            OfflineServers = 1,
            TotalProjects = 3,
            RecentRuns = [Run(3, PipelineStatus.Success)],
            Servers = [],
            Projects = []
        });

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var badges = cut.FindAll(".dashboard-tile .omni-badge");
            Assert.True(badges.Count >= 4, cut.Markup);
            Assert.All(badges, badge => Assert.DoesNotContain("omni-badge--width-", badge.ClassName, StringComparison.Ordinal));
        });
    }
    [Fact]
    public void DoesNotRenderOrLoadAiRunsTile()
    {
        _handler.SetJsonResponse("monitoring/dashboard", new DashboardOverviewDto
        {
            Servers = [],
            RecentRuns = []
        });

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Dashboard", StringComparison.Ordinal));

        Assert.DoesNotContain("AiRunsThisWeek", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/ai-tasks\"", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Url.Contains("api/ai", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RedirectsToLogin_WhenNotAuthenticated()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: false);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = Render<Home>();

        Assert.EndsWith("/login", nav.Uri);
    }

    [Fact]
    public void NoReadableResource_RendersWithoutDataRequests()
    {
        Services.GetRequiredService<PermissionService>().SetPermissions([], false);

        var cut = Render<Home>();

        Assert.DoesNotContain(_handler.Requests, request =>
            request.Url.Contains("monitoring/dashboard", StringComparison.Ordinal)
            || request.Url.Contains("api/appmonitoring/summary", StringComparison.Ordinal));
        // R-43: never "Access denied" on Home; with no organization and no notification it says so.
        Assert.DoesNotContain("AccessDenied", cut.Markup);
        Assert.Contains("DashboardNothingToShow", cut.Markup);
    }

    [Fact]
    public void Renders_RecentRuns_WithBadges()
    {
        _handler.SetJsonResponse("monitoring/dashboard", new DashboardOverviewDto
        {
            TotalServers = 1,
            OnlineServers = 1,
            OfflineServers = 0,
            PendingTasks = 0,
            Servers = [new ServerDto { Id = 1, Name = "srv", Status = ServerStatus.Online }],
            RecentRuns =
            [
                new PipelineRunDto
                {
                    Id = 1,
                    PipelineId = 10,
                    PipelineName = "deploy",
                    Status = PipelineStatus.Success,
                    StartedAt = DateTime.UtcNow.AddMinutes(-1),
                    Steps = [new PipelineStepRunDto { Id = 1, TriggeredRunId = 2 }]
                },
                new PipelineRunDto
                {
                    Id = 2,
                    PipelineId = 11,
                    PipelineName = "verify",
                    Status = PipelineStatus.Success,
                    StartedAt = DateTime.UtcNow
                }
            ]
        });

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("deploy"));

        Assert.Contains("deploy", cut.Markup);
        Assert.DoesNotContain("verify", cut.Markup);

        cut.Find("button.omni-data-grid__expand").Click();
        cut.WaitForState(() => cut.Markup.Contains("verify"));

        Assert.Contains("verify", cut.Markup);
        var runsGrid = cut.FindComponent<PipelineRunsGrid>().Instance;
        Assert.True(runsGrid.Compact);
        Assert.True(runsGrid.ShowDurationInCompact);
        Assert.Equal(10, runsGrid.MaxGroups);
    }

    [Fact]
    public void Handles_HttpRequestException()
    {
        _handler.SetResponse("api/", System.Net.HttpStatusCode.InternalServerError);

        // Should not throw - the page catches HttpRequestException and renders with a null
        // dashboard, so the stat cards fall back to the "-" placeholder.
        var cut = Render<Home>();
        Assert.Contains("Dashboard", cut.Markup);
        Assert.Contains("-", cut.Markup);
    }

    [Fact]
    public void StatusColumn_OnlineServer_RendersWifiIconWithSuccessColorAndAriaLabel()
    {
        RenderWithServer(new ServerDto { Id = 1, Name = "srv", Status = ServerStatus.Online });

        var cut = Render<Home>();
        var statusIcon = cut.WaitForElement("svg.omni-icon.omni-u-text-success[role='img'][aria-label]");
        var classes = statusIcon.GetAttribute("class") ?? string.Empty;

        // Check the row status icon, not the dashboard tiles which legitimately use both colours.
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.WifiHigh);
        Assert.Contains("omni-u-text-success", classes);
        Assert.DoesNotContain("omni-u-text-danger", classes);
        // Icon-only status carries an accessible name (a11y), not just a title.
        Assert.False(string.IsNullOrWhiteSpace(statusIcon.GetAttribute("aria-label")));
    }

    [Fact]
    public void StatusColumn_OfflineServer_RendersWifiOffIconWithDangerColor()
    {
        RenderWithServer(new ServerDto { Id = 1, Name = "srv", Status = ServerStatus.Offline });

        var cut = Render<Home>();
        var statusIcon = cut.WaitForElement("svg.omni-icon.omni-u-text-danger[role='img'][aria-label]");
        var classes = statusIcon.GetAttribute("class") ?? string.Empty;

        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.WifiSlash);
        Assert.Contains("omni-u-text-danger", classes);
        Assert.DoesNotContain("omni-u-text-success", classes);
    }

    [Fact]
    public void StatusColumn_RendersCapabilityIcons_WhenFlagsSet()
    {
        RenderWithServer(new ServerDto
        {
            Id = 1,
            Name = "srv",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            DeploymentTargetAvailable = true,
            PackageManagementAvailable = true
        });

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("omni-u-text-success"));

        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Wrench);
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.RocketLaunch);
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Settings);
    }

    [Theory]
    [InlineData("CapBuild", "pipelines")]
    [InlineData("CapDeploy", "apps")]
    [InlineData("CapManage", "modules")]
    public void CapabilityIcon_ClickNavigatesToMatchingServerSection(
        string ariaLabel,
        string section)
    {
        RenderWithServer(new ServerDto
        {
            Id = 42,
            Name = "srv",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            DeploymentTargetAvailable = true,
            PackageManagementAvailable = true
        });
        var cut = Render<Home>();
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var shortcut = cut.WaitForElement($".clickable-cell[aria-label='{ariaLabel}']");
        // SITE-FUNC-HOME-CAPABILITY-001: the row opens the overview; in the browser its handler ran
        // after the shortcut's and won, unless the shortcut stops the click and the key.
        RowEventIsolation.AssertRowDoesNotReceive(shortcut, "onclick", "onkeydown");

        shortcut.Click();

        Assert.EndsWith($"/servers/42/{section}", navigation.Uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CapBuild", "pipelines", "Enter")]
    [InlineData("CapDeploy", "apps", " ")]
    [InlineData("CapManage", "modules", "Enter")]
    public void CapabilityIcon_KeyboardActivation_NavigatesToMatchingServerSection_NotTheRow(
        string ariaLabel,
        string section,
        string key)
    {
        RenderWithServer(new ServerDto
        {
            Id = 42,
            Name = "srv",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            DeploymentTargetAvailable = true,
            PackageManagementAvailable = true
        });
        var cut = Render<Home>();
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        cut.WaitForElement($".clickable-cell[aria-label='{ariaLabel}']").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = key });

        Assert.EndsWith($"/servers/42/{section}", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void RowClick_StillOpensTheServerOverview()
    {
        RenderWithServer(new ServerDto { Id = 42, Name = "srv", Hostname = "srv", Status = ServerStatus.Online, PipelineRunnerEnabled = true });
        var cut = Render<Home>();
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        cut.WaitForElement(".clickable-cell[aria-label='CapBuild']").Closest("tr")!.Click();

        Assert.EndsWith("/servers/42/overview", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusColumn_OmitsCapabilityIcons_WhenFlagsUnset()
    {
        RenderWithServer(new ServerDto
        {
            Id = 1,
            Name = "srv",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = false,
            DeploymentTargetAvailable = false,
            PackageManagementAvailable = false
        });

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("omni-u-text-success"));

        // Only the online/offline icon is shown when no capability is known. rocket_launch is unique to
        // the deploy capability inside the servers grid, so its absence there proves the conditional
        // icons are gated on the flags. Scoped to that grid: the first-run onboarding card renders its
        // own rocket_launch heading icon elsewhere on the page.
        var serversGrid = cut.FindComponent<OmniDataGrid<ServerDto>>();
        Assert.DoesNotContain("rocket_launch", serversGrid.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("build", serversGrid.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("settings", serversGrid.Markup, StringComparison.Ordinal);
    }

    private void RenderWithServer(ServerDto server) =>
        _handler.SetJsonResponse("monitoring/dashboard", new DashboardOverviewDto
        {
            TotalServers = 1,
            OnlineServers = server.Status == ServerStatus.Online ? 1 : 0,
            OfflineServers = server.Status == ServerStatus.Online ? 0 : 1,
            PendingTasks = 0,
            Servers = [server],
            RecentRuns = []
        });
}

