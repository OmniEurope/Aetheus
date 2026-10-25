// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerOverviewDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerOverviewDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeServer(
        bool pipelineRunnerEnabled = false,
        bool metricsReceived = true,
        string? agentVersion = "1.2.3.4",
        DateTime? agentInstalledAt = null) => new()
        {
            Id = 1,
            Name = "prod-srv",
            Hostname = "prod.example.com",
            IpAddress = "10.0.1.1",
            OsDescription = "Ubuntu 22.04",
            AgentVersion = agentVersion ?? string.Empty,
            AgentInstalledAt = agentInstalledAt,
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            CpuPercent = 45.5,
            MemoryUsedMb = 2048,
            MemoryTotalMb = 8192,
            DiskUsedGb = 50,
            DiskTotalGb = 200,
            LastHeartbeat = DateTime.Now.AddMinutes(-5),
            CreatedAt = DateTime.Now.AddDays(-30),
            Tags = ["production", "web"],
            PipelineRunnerEnabled = pipelineRunnerEnabled,
            Services =
        [
            new ServiceInfoDto { Name = "nginx", IsRunning = true },
            new ServiceInfoDto { Name = "postgres", IsRunning = true },
            new ServiceInfoDto { Name = "redis", IsRunning = false }
        ],
            Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
            Apache = new ApacheDataDto { Modules = [], VirtualHosts = [] }
        };

    private IRenderedComponent<ServerOverviewSection> RenderSection(ServerDetailDto? server = null)
    {
        var srv = server ?? MakeServer();
        _handler.SetJsonResponse($"api/servers/{srv.Id}/pipeline-runner", (ServerDto)srv);
        return Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, srv)
            .Add(x => x.MetricsReceived, true));
    }

    [Fact]
    public void Renders_ServerEssentials()
    {
        var cut = RenderSection();
        Assert.Contains("prod.example.com", cut.Markup);
        Assert.Contains("Ubuntu 22.04", cut.Markup);
    }

    [Fact]
    public void Renders_Tags_WhenPresent()
    {
        var cut = RenderSection();
        Assert.Contains("production", cut.Markup);
        Assert.Contains("web", cut.Markup);
    }

    [Fact]
    public void Renders_Services_Count()
    {
        var cut = RenderSection();
        // 2 running / 3 total
        Assert.Contains("2", cut.Markup);
        Assert.Contains("3", cut.Markup);
    }

    [Fact]
    public void Renders_PipelineRunner_Disabled_State()
    {
        ServerOverviewTabsTests.OpenTab(this, "runner");
        var cut = RenderSection(MakeServer(pipelineRunnerEnabled: false));
        Assert.Contains("PipelineRunnerDisabledHint", cut.Markup);
    }

    [Fact]
    public void Renders_PipelineRunner_Enabled_State()
    {
        ServerOverviewTabsTests.OpenTab(this, "runner");
        var cut = RenderSection(MakeServer(pipelineRunnerEnabled: true));
        Assert.Contains("PipelineRunnerEnabledHint", cut.Markup);
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData("", "-")]
    [InlineData("1.2.3.4", "v1.2.3")]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("1.2", "v1.2")]
    public void FormatAgentVersion_ReturnsExpected(string? version, string expected)
    {
        var method = typeof(ServerOverviewSection).GetMethod("FormatAgentVersion", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)method.Invoke(null, [version])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Renders_AgentInstalledAt_AsRelativeTime_WhenRecent()
    {
        var recentInstall = DateTime.Now.AddDays(-10);
        var cut = RenderSection(MakeServer(agentInstalledAt: recentInstall));
        // A 10-day-old install is recent: the >90-day "Stale" badge must not appear.
        Assert.DoesNotContain("Stale", cut.Markup);
    }

    [Fact]
    public void Renders_StaleBadge_WhenAgentInstalledMoreThan90DaysAgo()
    {
        var oldInstall = DateTime.Now.AddDays(-91);
        var cut = RenderSection(MakeServer(agentInstalledAt: oldInstall));
        Assert.Contains("Stale", cut.Markup);
    }

    [Fact]
    public void Renders_NoAgentInstallDate_ShowsDash()
    {
        var cut = RenderSection(MakeServer(agentInstalledAt: null));
        Assert.Contains("-", cut.Markup);
    }

    [Fact]
    public async Task TogglePipelineRunnerAsync_WhenBusy_Returns()
    {
        var cut = RenderSection();
        typeof(ServerOverviewSection).GetField("_runnerBusy", Priv)!.SetValue(cut.Instance, true);

        var method = typeof(ServerOverviewSection).GetMethod("TogglePipelineRunnerAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Should still be busy (no API called)
        var busy = (bool)typeof(ServerOverviewSection).GetField("_runnerBusy", Priv)!.GetValue(cut.Instance)!;
        Assert.True(busy);
    }

    [Fact]
    public async Task TogglePipelineRunnerAsync_Disable_DoesNotConfirm()
    {
        // Runner is enabled → disabling requires no confirmation
        _handler.SetJsonResponse("api/servers/1/pipeline-runner", (ServerDto)MakeServer(pipelineRunnerEnabled: false));
        var cut = RenderSection(MakeServer(pipelineRunnerEnabled: true));

        var method = typeof(ServerOverviewSection).GetMethod("TogglePipelineRunnerAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Disable path skips the confirm dialog and POSTs straight to the pipeline-runner endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/1/pipeline-runner"));
        Assert.False((bool)typeof(ServerOverviewSection).GetField("_runnerBusy", Priv)!.GetValue(cut.Instance)!);
    }
}
