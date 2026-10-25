// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerOverviewSectionTests : BunitContext
{
    public ServerOverviewSectionTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_OverviewSection()
    {
        var server = new ServerDetailDto
        {
            Id = 1,
            Name = "web-01",
            Status = ServerStatus.Online,
            Type = ServerType.Normal
        };
        var cut = Render<ServerOverviewSection>(p =>
            p.Add(x => x.Server, server)
             .Add(x => x.MetricsReceived, true));
        // With metrics received the CPU/Memory/Disk tiles render their numeric value elements.
        // (The redundant "Overview" H5 heading was removed - the Essentials card is the section's anchor.)
        Assert.Contains("essentials-grid", cut.Markup);
        Assert.Contains("metric-value", cut.Markup);
    }

    [Fact]
    public void Renders_OverviewSection_NoMetrics()
    {
        var server = new ServerDetailDto
        {
            Id = 1,
            Name = "web-01",
            Status = ServerStatus.Offline,
            Type = ServerType.Build
        };
        var cut = Render<ServerOverviewSection>(p =>
            p.Add(x => x.Server, server)
             .Add(x => x.MetricsReceived, false));
        // Without metrics the CPU/Memory/Disk tiles show branded loaders instead of values.
        Assert.Contains("essentials-grid", cut.Markup);
        Assert.Contains("aetheus-loader-logo", cut.Markup);
        Assert.DoesNotContain("CleanupApplyMode", cut.Markup);
    }

    [Fact]
    public void Renders_StorageDiagnostics_WithReclaimableAndGrowthTrend()
    {
        var now = DateTime.UtcNow;
        var server = new ServerDetailDto
        {
            Id = 1,
            Name = "builder-01",
            Status = ServerStatus.Online,
            Type = ServerType.Build,
            StorageDiagnostics = new StorageDiagnosticsDto
            {
                BuildCacheAvailable = true,
                DockerInventoryAvailable = true,
                BuildCacheBytes = 12L * 1024 * 1024 * 1024,
                BuildCacheReclaimableBytes = 8L * 1024 * 1024 * 1024,
                CollectedAtUtc = now
            }
        };
        var metrics = new List<ServerMetricDto>
        {
            new() { BuildCacheAvailable = true, BuildCacheBytes = 10L * 1024 * 1024 * 1024, Timestamp = now.AddHours(-23) },
            new() { BuildCacheAvailable = true, BuildCacheBytes = 12L * 1024 * 1024 * 1024, Timestamp = now }
        };

        ServerOverviewTabsTests.OpenTab(this, "storage");
        var cut = Render<ServerOverviewSection>(parameters => parameters
            .Add(component => component.Server, server)
            .Add(component => component.Metrics, metrics));

        Assert.Contains("StorageDiagnostics", cut.Markup);
        Assert.Matches(@"8 Go", cut.Markup);
        Assert.Matches(@"\+2 Go", cut.Markup);
    }

    [Fact]
    public void Renders_Unavailable_WhenDockerInventoryFails()
    {
        var server = new ServerDetailDto
        {
            Id = 1,
            Name = "builder-01",
            Status = ServerStatus.Online,
            Type = ServerType.Build,
            StorageDiagnostics = new StorageDiagnosticsDto
            {
                CollectedAtUtc = DateTime.UtcNow,
                BuildCacheAvailable = false,
                DockerInventoryAvailable = false
            }
        };

        ServerOverviewTabsTests.OpenTab(this, "storage");
        var cut = Render<ServerOverviewSection>(parameters => parameters
            .Add(component => component.Server, server));

        var values = cut.FindAll(".storage-diagnostic-grid .essential-value");
        Assert.Equal(6, values.Count);
        Assert.Equal("Unavailable", values[0].TextContent.Trim());
        Assert.Equal("Unavailable", values[1].TextContent.Trim());
        Assert.Equal("Unavailable", values[3].TextContent.Trim());
        Assert.Equal("Unavailable", values[4].TextContent.Trim());
    }
}
