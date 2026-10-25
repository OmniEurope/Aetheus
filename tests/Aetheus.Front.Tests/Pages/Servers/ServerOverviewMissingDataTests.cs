// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Resources;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>A completed load without agent metrics must show unavailable, and DateTime.MinValue
/// must not render as a year-one date.</summary>
public sealed class ServerOverviewMissingDataTests : BunitContext
{
    public ServerOverviewMissingDataTests() => BunitTestHelper.RegisterServices(this);

    private static ServerDetailDto NeverReportedServer() => new()
    {
        Id = 3,
        Name = "offline-01",
        Status = ServerStatus.Offline,
        Type = ServerType.Normal
    };

    [Fact]
    public void MetricTiles_Load_UntilTheInitialLoadCompletes()
    {
        var cut = Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, NeverReportedServer())
            .Add(x => x.MetricsReceived, false)
            .Add(x => x.MetricsLoadCompleted, false));

        Assert.Equal(3, cut.FindAll(".metric-tile-grid .aetheus-loader-logo").Count);
    }

    [Fact]
    public void MetricTiles_ReadUnavailable_OnceTheLoadCompletedWithoutMetrics()
    {
        var cut = Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, NeverReportedServer())
            .Add(x => x.MetricsReceived, false)
            .Add(x => x.MetricsLoadCompleted, true));

        Assert.Empty(cut.FindAll(".metric-tile-grid .aetheus-loader-logo"));
        Assert.Equal(3, cut.FindAll(".metric-tile-grid p").Count(p => p.TextContent.Trim() == "Unavailable"));
    }

    [Fact]
    public void ServicesTile_IsItselfTheLinkInTheTileGrid_NotWrappedInOne()
    {
        var cut = Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, NeverReportedServer())
            .Add(x => x.MetricsReceived, false)
            .Add(x => x.MetricsLoadCompleted, true));

        // Five tiles, all direct children of one OE tile grid (same size), the Services one a real link.
        Assert.Equal(5, cut.FindAll(".omni-tile-grid.metric-tile-grid > .omni-card").Count);
        var services = cut.Find(".metric-tile-grid > a.omni-card--action");
        Assert.Equal("/servers/3/services", services.GetAttribute("href"));
        Assert.Single(cut.FindAll(".metric-tile-grid a"));
    }

    [Fact]
    public void StorageNeverCollected_WithMetricHistory_ShowsNeverAndNoYearOne()
    {
        var localizer = Services.GetRequiredService<IStringLocalizer<AppStrings>>();
        localizer["StorageDiagnosticsCollectedAt"].Returns(new LocalizedString("StorageDiagnosticsCollectedAt", "Last collected: {0}"));
        var now = DateTime.Now;
        var metrics = new List<ServerMetricDto>
        {
            new() { BuildCacheAvailable = true, BuildCacheBytes = 1024L * 1024 * 1024, Timestamp = now.AddHours(-2) },
            new() { BuildCacheAvailable = true, BuildCacheBytes = 2L * 1024 * 1024 * 1024, Timestamp = now }
        };

        ServerOverviewTabsTests.OpenTab(this, "storage");
        var cut = Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, NeverReportedServer())
            .Add(x => x.Metrics, metrics));

        var card = cut.Find(".storage-overview-card");
        Assert.Contains("Last collected: Never", card.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("0001", card.TextContent, StringComparison.Ordinal);
        var agentAndNuGet = cut.FindAll(".storage-diagnostic-grid > div")
            .Single(cell => cell.TextContent.Contains("AgentAndNuGet", StringComparison.Ordinal));
        Assert.Contains("Unavailable", agentAndNuGet.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("MiB", agentAndNuGet.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverReportedHeartbeat_ShowsADash()
    {
        var cut = Render<ServerOverviewSection>(p => p.Add(x => x.Server, NeverReportedServer()));

        var heartbeat = cut.FindAll(".essential-item")
            .Single(item => item.TextContent.Contains("LastHeartbeat", StringComparison.Ordinal));
        Assert.Equal("-", heartbeat.QuerySelector(".essential-value")!.TextContent.Trim());
    }
}
