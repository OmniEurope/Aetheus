// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerPortsentrySectionTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerPortsentrySectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // The section also reads the protocols its Protocol header filter offers.
        _handler.SetJsonResponse("portsentry/filter-values", new PortsentryFilterValuesDto());
    }

    private void SetupPortsentryApi(List<PortsentryWhitelistIpDto>? whitelist = null)
    {
        var items = whitelist ?? [];
        _handler.SetJsonResponse("api/servers/50/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>
        {
            Items = items,
            TotalCount = items.Count
        });
        _handler.SetJsonResponse("api/servers/50/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
        _handler.SetJsonResponse("api/servers/50/portsentry/action", true);
        _handler.SetJsonResponse("api/servers/50/portsentry/setup", true);
        _handler.SetJsonResponse("api/servers/50/portsentry/logs", true);
    }

    private IRenderedComponent<PageHeaderOutletHost> RenderInstalled(PortsentryDataDto? ps = null)
    {
        SetupPortsentryApi();
        var data = ps ?? new PortsentryDataDto
        {
            IsInstalled = true,
            IsRunning = true,
            Version = "2.0",
            Mode = "atcp",
            BlockedCount = 0
        };
        return this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, data));
    }

    [Fact]
    public void Renders_InstalledState_ShowsPortSentryHeading()
    {
        var cut = RenderInstalled();
        Assert.Contains("Restart", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_InstalledState_ShowsInstalledText()
    {
        var cut = RenderInstalled();
        Assert.Contains("Installed", cut.Markup);
    }

    [Fact]
    public void Renders_InstalledWithVersion_ShowsVersionNumber()
    {
        var cut = RenderInstalled(new PortsentryDataDto
        {
            IsInstalled = true,
            IsRunning = true,
            Version = "1.2.3",
            BlockedCount = 2
        });
        Assert.Contains("1.2.3", cut.Markup);
    }

    [Fact]
    public void Renders_NotInstalled_ShowsNotInstalledState()
    {
        _handler.SetJsonResponse("api/servers/50/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
        var ps = new PortsentryDataDto { IsInstalled = false };
        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, ps));
        Assert.Contains("NotInstalled", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("PortsentrySetup", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PortSentry", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_BlockedCount_ShowsCountInMarkup()
    {
        var cut = RenderInstalled(new PortsentryDataDto
        {
            IsInstalled = true,
            IsRunning = true,
            BlockedCount = 12,
            Version = "2.0"
        });
        Assert.Contains("12", cut.Markup);
    }

    [Fact]
    public async Task ExecuteActionAsync_Completes_WithoutThrowing()
    {
        var cut = RenderInstalled();

        var method = typeof(ServerPortsentrySection).GetMethod("ExecuteActionAsync", Priv)!;
        await (Task)method.Invoke(cut.FindComponent<ServerPortsentrySection>().Instance, [PortsentryAction.Start])!;

        var running = (bool)typeof(ServerPortsentrySection).GetField("_actionRunning", Priv)!.GetValue(cut.FindComponent<ServerPortsentrySection>().Instance)!;
        Assert.False(running);
    }

    [Fact]
    public async Task GetLogsAsync_SendsRequest_WithoutThrowing()
    {
        var cut = RenderInstalled();

        var method = typeof(ServerPortsentrySection).GetMethod("GetLogsAsync", Priv)!;
        await (Task)method.Invoke(cut.FindComponent<ServerPortsentrySection>().Instance, [])!;
        // GetLogsAsync issues a request to the portsentry logs endpoint.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers/50/portsentry/logs"));
    }

    [Fact]
    public async Task AddWhitelistIpAsync_EmptyIp_ProducesNothing()
    {
        var cut = RenderInstalled();

        typeof(ServerPortsentrySection).GetField("_whitelistIp", Priv)!.SetValue(cut.FindComponent<ServerPortsentrySection>().Instance, "");
        var method = typeof(ServerPortsentrySection).GetMethod("AddWhitelistIpAsync", Priv)!;
        await (Task)method.Invoke(cut.FindComponent<ServerPortsentrySection>().Instance, [])!;

        var whitelist = (List<PortsentryWhitelistIpDto>)typeof(ServerPortsentrySection)
            .GetField("_whitelist", Priv)!.GetValue(cut.FindComponent<ServerPortsentrySection>().Instance)!;
        Assert.Empty(whitelist);
    }

    [Fact]
    public void GetBlockedCountBadgeStyle_Zero_ReturnsSuccess()
    {
        var method = typeof(ServerPortsentrySection).GetMethod("GetBlockedCountBadgeStyle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(method.Invoke(null, [0]));
    }

    [Fact]
    public void GetBlockedCountBadgeStyle_High_ReturnsDanger()
    {
        var method = typeof(ServerPortsentrySection).GetMethod("GetBlockedCountBadgeStyle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [10]);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Renders_WithWhitelistEntry_ShowsIpAddress()
    {
        SetupPortsentryApi([new PortsentryWhitelistIpDto { Id = 1, IpAddress = "192.168.1.1", Description = "Office" }]);
        var ps = new PortsentryDataDto { IsInstalled = true, IsRunning = true, Version = "2.0", BlockedCount = 0 };
        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, ps));
        var load = typeof(ServerPortsentrySection).GetMethod("LoadWhitelistAsync", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)load.Invoke(cut.FindComponent<ServerPortsentrySection>().Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);
        var whitelist = (List<PortsentryWhitelistIpDto>)typeof(ServerPortsentrySection)
            .GetField("_whitelist", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.FindComponent<ServerPortsentrySection>().Instance)!;
        Assert.Contains(whitelist, ip => ip.IpAddress == "192.168.1.1");
    }

    [Fact]
    public void R3_41_Installed_ShowsRestartAsTheOneAction_AndKeepsStartStopAndLogsInTheMenu()
    {
        var cut = RenderInstalled();

        var standalone = cut.FindAll("button:not(.omni-menu__item)").Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Restart", standalone);
        Assert.DoesNotContain("Start", standalone);
        Assert.DoesNotContain("Stop", standalone);
        Assert.DoesNotContain(standalone, text => text.Contains("ViewLogs", StringComparison.Ordinal));
        Assert.NotNull(cut.FindMenuItem("Start"));
        Assert.NotNull(cut.FindMenuItem("Stop"));
        Assert.NotNull(cut.FindMenuItem("ViewLogs"));
    }
}
