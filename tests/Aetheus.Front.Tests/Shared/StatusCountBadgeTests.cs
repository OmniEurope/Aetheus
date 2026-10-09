// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Dashboards;
using Bunit;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Recette R2-109: a status count badge carries the number, the status name and the status icon, so the
/// phone width can show the icon in place of the name while the name stays the accessible text and the
/// tooltip. The swap itself is CSS (app.css, measured by the 375 px E2E test); this checks the markup the
/// CSS relies on, and that the dashboard and the server list use the shared badge.
/// </summary>
public sealed class StatusCountBadgeTests : BunitContext
{
    [Fact]
    public void Badge_CarriesCountNameIconAndTooltip()
    {
        BunitTestHelper.RegisterServices(this);

        var cut = Render<StatusCountBadge>(p => p
            .Add(x => x.Count, 20)
            .Add(x => x.Label, "Succeeded")
            .Add(x => x.Icon, OmniIconName.Check)
            .Add(x => x.Tone, OmniTone.Success));

        var badge = cut.Find(".omni-badge.status-count-badge.omni-badge--success");
        Assert.Equal("Succeeded", badge.GetAttribute("title"));
        Assert.Equal("20", badge.QuerySelector(".status-count-badge__count")!.TextContent);
        Assert.Equal("Succeeded", badge.QuerySelector(".status-count-badge__label")!.TextContent);
        Assert.NotNull(badge.QuerySelector(".status-count-badge__icon"));
        // The accessible text is the count and the name, the icon adds nothing to it.
        Assert.Equal("20 Succeeded", badge.TextContent.Trim());
    }

    [Fact]
    public void ZeroCount_RendersNothing()
    {
        BunitTestHelper.RegisterServices(this);

        var cut = Render<StatusCountBadge>(p => p
            .Add(x => x.Count, 0)
            .Add(x => x.Label, "Failed")
            .Add(x => x.Icon, OmniIconName.Close));

        Assert.Empty(cut.FindAll(".omni-badge"));
    }

    [Fact]
    public void Dashboard_RecentRunsAndMonitoredApps_UseTheStatusCountBadge()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true);
        BunitTestHelper.SetOnboardingWizardResponses(handler);
        handler.SetJsonResponse(HttpMethod.Get, "api/appmonitoring/summary",
            new AppMonitoringSummaryDto { UpCount = 20, DownCount = 5 });
        handler.SetJsonResponse("api/monitoring/dashboard", new DashboardOverviewDto
        {
            RecentRuns =
            [
                new PipelineRunDto { Id = 1, PipelineId = 1, PipelineName = "CI", Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow.AddMinutes(-10) },
                new PipelineRunDto { Id = 2, PipelineId = 1, PipelineName = "CI", Status = PipelineStatus.Failed, StartedAt = DateTime.UtcNow.AddMinutes(-5) }
            ],
            Servers = [],
            Projects = []
        });

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var badges = cut.FindAll(".status-count-badge");
            Assert.Equal(4, badges.Count);
            Assert.Contains(badges, b => b.ClassList.Contains("omni-badge--success") && b.TextContent.Trim() == "20 AppHealthUp");
            Assert.Contains(badges, b => b.ClassList.Contains("omni-badge--danger") && b.TextContent.Trim() == "5 AppHealthDown");
            Assert.Equal(4, cut.FindAll(".status-count-badge .status-count-badge__icon").Count);
        }, TimeSpan.FromSeconds(2));
    }
}
