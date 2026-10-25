// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerPortsentryRkhunterTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerPortsentryRkhunterTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // The section also reads the protocols its Protocol header filter offers.
        _handler.SetJsonResponse("portsentry/filter-values", new PortsentryFilterValuesDto());
        // An installed RKHunter section loads its scan history on first render too.
        _handler.SetJsonResponse("api/servers/8/rkhunter/history", new List<RkhunterScanResultDto>());
    }

    // ──── Portsentry ────

    [Fact]
    public void Portsentry_NotInstalled_RendersNotInstalledState()
    {
        _handler.SetJsonResponse("api/servers/7/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());

        var ps = new PortsentryDataDto { IsInstalled = false };
        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 7)
            .Add(x => x.Ps, ps));

        Assert.Contains("NotInstalled", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("PortsentrySetup", cut.Markup, StringComparison.Ordinal);
        // Title heading removed (the page header shows it); the PortSentry split button is installed-only.
        Assert.DoesNotContain("PortSentry", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Portsentry_Installed_RendersStatusAndBlockedCount()
    {
        _handler.SetJsonResponse("api/servers/7/portsentry/whitelist",
            new PaginatedResult<PortsentryWhitelistIpDto>
            {
                Items = [new() { Id = 1, IpAddress = "192.168.1.10", Description = "Dev box" }],
                TotalCount = 1
            });
        _handler.SetJsonResponse("api/servers/7/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());

        var ps = new PortsentryDataDto
        {
            IsInstalled = true,
            IsRunning = true,
            Version = "1.2",
            Mode = "atcp",
            BlockedCount = 3
        };
        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 7)
            .Add(x => x.Ps, ps));

        Assert.Contains("Restart", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Installed", cut.Markup);
        Assert.Contains("v1.2", cut.Markup);
    }

    // SITE-FUNC-PORTSENTRY-001: the agent sends DateTime.MinValue when the blocked file has no date; the row
    // shows a dash, never "1/1/0001", and a real date stays shown.
    [Fact]
    public void Portsentry_BlockedRow_WithoutADate_ShowsADash()
    {
        var blockedAt = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc);
        _handler.SetJsonResponse("api/servers/7/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
        _handler.SetJsonResponse("api/servers/7/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>
        {
            Items =
            [
                new() { IpAddress = "203.0.113.66", Protocol = "TCP", Reason = "Blocked by PortSentry" },
                new() { IpAddress = "203.0.113.67", Protocol = "TCP", BlockedAt = blockedAt, Reason = "Blocked by PortSentry" }
            ],
            TotalCount = 2
        });

        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 7)
            .Add(x => x.Ps, new PortsentryDataDto { IsInstalled = true, IsRunning = true, BlockedCount = 2 }));

        cut.WaitForAssertion(() =>
        {
            var undated = cut.FindAll("tr").Single(row => row.TextContent.Contains("203.0.113.66", StringComparison.Ordinal));
            var dated = cut.FindAll("tr").Single(row => row.TextContent.Contains("203.0.113.67", StringComparison.Ordinal));
            Assert.Contains(undated.QuerySelectorAll("td"), cell => cell.TextContent.Trim() == "-");
            Assert.DoesNotContain("0001", undated.TextContent, StringComparison.Ordinal);
            Assert.Contains(blockedAt.ToLocalTime().ToString("g"), dated.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Portsentry_BlockedCountBadgeStyle_ForZeroShowsSuccess()
    {
        _handler.SetJsonResponse("api/servers/7/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
        _handler.SetJsonResponse("api/servers/7/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
        var ps = new PortsentryDataDto { IsInstalled = true, IsRunning = true, BlockedCount = 0 };
        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 7)
            .Add(x => x.Ps, ps));
        Assert.Contains("BlockedIps", cut.Markup);
    }

    [Fact]
    public void Portsentry_ManyBlocked_RendersDangerBadge()
    {
        _handler.SetJsonResponse("api/servers/7/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
        _handler.SetJsonResponse("api/servers/7/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
        var ps = new PortsentryDataDto { IsInstalled = true, IsRunning = true, BlockedCount = 25 };
        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 7)
            .Add(x => x.Ps, ps));
        Assert.Contains("25", cut.Markup);
    }

    // ──── Rkhunter ────

    [Fact]
    public void Rkhunter_NotInstalled_RendersNotInstalledState()
    {
        _handler.SetJsonResponse("api/servers/8/rkhunter/warnings", new List<RkhunterWarningDto>());
        var rk = new RkhunterDataDto { IsInstalled = false };
        var cut = this.RenderWithPageHeader<ServerRkhunterSection>(p => p
            .Add(x => x.ServerId, 8)
            .Add(x => x.Rk, rk));

        Assert.Contains("NotInstalled", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("RkhunterSetup", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("RKHunter", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Rkhunter_Installed_RendersVersion()
    {
        _handler.SetJsonResponse("api/servers/8/rkhunter/warnings", new List<RkhunterWarningDto>());
        var rk = new RkhunterDataDto
        {
            IsInstalled = true,
            Version = "1.4.6",
            LastScanTime = DateTime.UtcNow.AddDays(-1),
            WarningCount = 2
        };
        var cut = this.RenderWithPageHeader<ServerRkhunterSection>(p => p
            .Add(x => x.ServerId, 8)
            .Add(x => x.Rk, rk));

        Assert.Contains("v1.4.6", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("RKHunter", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Rkhunter_WithWarnings_RendersWarningCount()
    {
        _handler.SetJsonResponse("api/servers/8/rkhunter/warnings",
            new List<RkhunterWarningDto>
            {
                new() { Id = 1, Category = "rootkit", Detail = "suspicious file", Severity = "warning", FoundAt = DateTime.UtcNow }
            });
        var rk = new RkhunterDataDto
        {
            IsInstalled = true,
            Version = "1.4.6",
            WarningCount = 3
        };
        var cut = this.RenderWithPageHeader<ServerRkhunterSection>(p => p
            .Add(x => x.ServerId, 8)
            .Add(x => x.Rk, rk));

        Assert.Contains("3", cut.Markup);
    }
}
