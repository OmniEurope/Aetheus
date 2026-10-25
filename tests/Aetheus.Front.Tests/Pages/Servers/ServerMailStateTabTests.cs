// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R1-29: the mail status has its own State tab, the tabs open on the accounts then the
/// domains, and a stack that is not compliant also shows its failing lines at the top of the page.</summary>
public sealed class ServerMailStateTabTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerMailStateTabTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static MailDiagnosticItemDto[] Compliant() =>
    [
        new() { Key = "services", Verdict = MailCheckVerdict.Ok },
        new() { Key = "hostname-dns", Verdict = MailCheckVerdict.Ok },
        new() { Key = "reverse-dns", Verdict = MailCheckVerdict.Ok },
        new() { Key = "tls", Verdict = MailCheckVerdict.Ok },
        new() { Key = "mx:example.com", Verdict = MailCheckVerdict.Ok },
        new() { Key = "spf:example.com", Verdict = MailCheckVerdict.Ok }
    ];

    private static MailDiagnosticItemDto[] WithRedTls() =>
    [
        .. Compliant().Where(item => item.Key != "tls"),
        new() { Key = "tls", Verdict = MailCheckVerdict.Missing, Detail = "no-certificate" }
    ];

    private IRenderedComponent<ServerMailSection> RenderOn(MailDiagnosticItemDto[] diagnostics, string? tab = null)
    {
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items = [new() { Id = 1, Name = "example.com", IsActive = true, DkimSelector = "default" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/10/mail/accounts", new PaginatedResult<MailAccountDto>
        {
            Items = [new() { Id = 1, Email = "admin@example.com", IsActive = true, QuotaMb = 1024 }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/10/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto
        {
            CheckedAt = DateTime.UtcNow,
            Items = [.. diagnostics]
        });
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo(tab is null ? "/servers/10/mail" : $"/servers/10/mail?tab={tab}");
        var server = ServerTestData.MakeMailServer();
        return Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));
    }

    [Fact]
    public void TheTabs_ListTheAccountsFirst_ThenTheDomains_ThenTheState()
    {
        var cut = RenderOn(Compliant());

        var tabs = cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()).ToList();

        Assert.Equal(["MailAccounts", "MailDomains", "MailStatusTab", "Aliases", "MailSpamFilter", "MailQueue", "Logs", "Relations"], tabs);
        Assert.Equal(["accounts", "domains", "status", "aliases", "spam", "queue", "logs", "relations"], ServerMailSection.TabSlugs);
    }

    [Fact]
    public void TheDefaultTab_IsTheAccounts_AndTheStatusIsNoLongerAboveThem()
    {
        var cut = RenderOn(Compliant());

        // The page opens on the accounts grid; the status card (with its "Provisioned by Aetheus" badge, the
        // DNS summary, the test and restart actions) lives in its own tab, not yet built.
        Assert.Contains("admin@example.com", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".mail-status"));
    }

    [Fact]
    public void TheStateTab_ShowsTheStatusCard_WithItsLinesAndActions()
    {
        var cut = RenderOn(Compliant(), tab: "status");

        var card = cut.Find(".mail-status");
        Assert.Contains("MailStatusTitle", card.TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(card.QuerySelectorAll(".mail-check-line"));
        // The one status card, not a second copy.
        Assert.Single(cut.FindAll(".mail-status"));
    }

    [Fact]
    public void ACompliantStack_ShowsNoBannerAtTheTopOfThePage()
    {
        var cut = RenderOn(Compliant());

        Assert.Empty(cut.FindAll(".mail-compliance-banner"));
    }

    [Fact]
    public void ANonCompliantStack_ShowsItsFailingLinesAtTheTopOfThePage_OnAnyOtherTab()
    {
        var cut = RenderOn(WithRedTls());

        var banner = cut.Find(".mail-compliance-banner");
        Assert.Contains("MailNotCompliantTitle", banner.TextContent, StringComparison.Ordinal);
        var lines = banner.QuerySelectorAll(".mail-check-line");
        Assert.Single(lines);
        Assert.Equal("fail", lines[0].GetAttribute("data-state"));
        // The passing lines are not repeated.
        Assert.DoesNotContain("example.com", banner.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBannerButton_LeadsToTheStateTab()
    {
        var cut = RenderOn(WithRedTls());
        var nav = Services.GetRequiredService<NavigationManager>();

        cut.Find(".mail-compliance-open").Click();

        Assert.EndsWith("/servers/10/mail?tab=status", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void OnTheStateTab_TheBannerStepsAside_SinceTheTabListsTheSameLines()
    {
        var cut = RenderOn(WithRedTls(), tab: "status");

        Assert.Empty(cut.FindAll(".mail-compliance-banner"));
        Assert.NotEmpty(cut.FindAll(".mail-status .mail-check-line[data-state=fail]"));
    }
}
