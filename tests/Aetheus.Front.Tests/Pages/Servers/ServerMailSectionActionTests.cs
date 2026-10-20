// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Drives the mail section's write actions. A mail domain or account is what routes real mail, so
/// creating one must reach the right endpoint on the right server, and deleting one must not happen
/// without a dialog. The dialog double returns a filled model so the handlers run past it.
/// </summary>
public sealed class ServerMailSectionActionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServerMailSectionActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private void StubReads()
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
        _handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto());
    }

    private IRenderedComponent<ServerMailSection> RenderSection(bool installed = true)
    {
        StubReads();
        var server = ServerTestData.MakeMailServer(installed, true, true);
        return Render<ServerMailSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, server.Id));
    }

    private static IElement? TryButton(IRenderedComponent<ServerMailSection> cut, string label) =>
        cut.FindAll("button").FirstOrDefault(b => b.Names().Contains(label, StringComparison.Ordinal));

    private static MailDialogModel Filled(MailDialogMode mode) => new()
    {
        Mode = mode,
        Domain = "new-domain.test",
        DkimSelector = "default",
        Email = "new@new-domain.test",
        Password = "Str0ng-Passw0rd!",
        Source = "alias@new-domain.test",
        Destination = "admin@example.com",
        DomainId = 1,
        QuotaMb = 2048,
        Hostname = "mail.new-domain.test"
    };

    [Fact]
    public void AnInstalledServerOffersTheMailWriteActions()
    {
        StubReads();
        var server = ServerTestData.MakeMailServer(true, true, true) with { MailSetupAvailable = true };
        var cut = Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));

        Assert.NotNull(TryButton(cut, "AddDomain"));
        // Mail service controls appear in the status list after counts are known.
        cut.WaitForAssertion(() => Assert.NotNull(TryButton(cut, "MailServiceActions")));
    }

    [Fact]
    public void AddingADomainWithoutConfirming_CallsNothing()
    {
        // The dialog is where the domain name is typed; dismissing it must not create anything.
        _dialog.OpenResult = null;
        var cut = RenderSection();
        var before = _handler.Requests.Count(r => r.Method == "POST");

        TryButton(cut, "AddDomain")!.Click();

        cut.WaitForAssertion(() => Assert.True(_dialog.OpenCount > 0));
        Assert.Equal(before, _handler.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public void ConfirmingADomainCreation_PostsToTheDomainsEndpointOfThatServer()
    {
        // A wrong server id here creates the domain on someone else's mail host.
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/servers/10/mail/domains", new MailDomainDto { Id = 9, Name = "new-domain.test" });
        _dialog.OpenResult = Filled(MailDialogMode.AddDomain);
        var cut = RenderSection();

        TryButton(cut, "AddDomain")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.EndsWith("servers/10/mail/domains", StringComparison.Ordinal)));
    }

    [Fact]
    public void CreatingADomain_ReloadsTheListSoTheNewRowAppears()
    {
        // Without the reload the user retypes a domain that already exists.
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/servers/10/mail/domains", new MailDomainDto { Id = 9, Name = "new-domain.test" });
        _dialog.OpenResult = Filled(MailDialogMode.AddDomain);
        var cut = RenderSection();
        var readsBefore = _handler.Requests.Count(r => r.Method == "GET");

        TryButton(cut, "AddDomain")!.Click();

        cut.WaitForAssertion(
            () => Assert.True(_handler.Requests.Count(r => r.Method == "GET") > readsBefore));
    }

    [Fact]
    public void AnUninstalledServerDoesNotOfferDomainCreation()
    {
        // Creating a domain on a host with no mail stack can only fail at the agent.
        var cut = RenderSection(installed: false);

        Assert.Null(TryButton(cut, "AddDomain"));
    }

    [Fact]
    public void TheSectionReadsDomainsAccountsAndAliasesOfItsOwnServer()
    {
        var cut = RenderSection();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_handler.Requests, r =>
                r.Url.Contains("servers/10/mail/domains", StringComparison.Ordinal));
        });
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("servers/11/", StringComparison.Ordinal));
    }

    [Fact]
    public void R3_41_AnAccountRow_KeepsTheMailboxVisible_AndDeleteInItsMenu()
    {
        var cut = RenderSection();
        cut.FindAll("[role='tab']").First(tab => tab.TextContent.Contains("MailAccounts", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("admin@example.com", cut.Markup, StringComparison.Ordinal));
        var row = cut.Find("button[title='MailboxOpen']").ParentElement!;
        var standalone = row.QuerySelectorAll("button:not(.omni-menu__item):not(.omni-overflow-menu__trigger)")
            .Select(b => b.GetAttribute("title")).ToList();
        Assert.Equal(["MailboxOpen"], standalone);
        Assert.NotNull(cut.FindMenuItem("Delete"));
    }

    private IRenderedComponent<ServerMailSection> RenderAliasesTab(params MailAliasDto[] aliases)
    {
        var cut = RenderSection();
        _handler.SetJsonResponse("api/servers/10/mail/aliases", new PaginatedResult<MailAliasDto> { Items = [.. aliases], TotalCount = aliases.Length });
        cut.FindAll("[role='tab']").First(tab => tab.TextContent.Contains("Aliases", StringComparison.Ordinal)).Click();
        return cut;
    }

    private int AliasReads => _handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("servers/10/mail/aliases", StringComparison.Ordinal));

    [Fact]
    public void ConfirmingANewAlias_PostsItUnderItsDomain_AndReloadsTheAliases()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/servers/10/mail/domains/1/aliases",
            new MailAliasDto { Id = 5, SourceEmail = "alias@new-domain.test", DestinationEmail = "admin@example.com" });
        _dialog.OpenResult = Filled(MailDialogMode.AddAlias);
        var cut = RenderAliasesTab();
        cut.WaitForAssertion(() => Assert.True(AliasReads > 0));
        var readsBefore = AliasReads;

        TryButton(cut, "AddAlias")!.Click();

        cut.WaitForAssertion(() => Assert.True(AliasReads > readsBefore));
        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST");
        Assert.EndsWith("servers/10/mail/domains/1/aliases", post.Url, StringComparison.Ordinal);
        Assert.Contains("alias@new-domain.test", post.Body, StringComparison.Ordinal);
        Assert.Contains("admin@example.com", post.Body, StringComparison.Ordinal);
        Assert.Equal("AddAlias", _dialog.LastTitle);
    }

    [Fact]
    public void ARefusedAlias_IsNotFollowedByAReload()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/10/mail/domains/1/aliases", new { }, System.Net.HttpStatusCode.BadRequest);
        _dialog.OpenResult = Filled(MailDialogMode.AddAlias);
        var cut = RenderAliasesTab();
        cut.WaitForAssertion(() => Assert.True(AliasReads > 0));
        var readsBefore = AliasReads;

        TryButton(cut, "AddAlias")!.Click();

        cut.WaitForAssertion(() => Assert.Single(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("/aliases", StringComparison.Ordinal)));
        Assert.Equal(readsBefore, AliasReads);
    }

    [Fact]
    public void DismissingTheAliasDialog_CreatesNothing()
    {
        _dialog.OpenResult = null;
        var cut = RenderAliasesTab();

        TryButton(cut, "AddAlias")!.Click();

        cut.WaitForAssertion(() => Assert.True(_dialog.OpenCount > 0));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void DeletingAnAlias_HappensOnlyOnceConfirmed(bool confirmed, int expectedDeletes)
    {
        _handler.SetJsonResponse(HttpMethod.Delete, "api/servers/10/mail/aliases/5", new { });
        _dialog.ConfirmResult = confirmed;
        var cut = RenderAliasesTab(new MailAliasDto { Id = 5, SourceEmail = "alias@example.com", DestinationEmail = "admin@example.com" });
        cut.WaitForAssertion(() => Assert.Contains("alias@example.com", cut.Markup, StringComparison.Ordinal));

        cut.FindAll("tr").Single(row => row.TextContent.Contains("alias@example.com", StringComparison.Ordinal)).QuerySelector("button[title='Delete']")!.Click();

        cut.WaitForAssertion(() => Assert.Equal("ConfirmDeleteAlias", _dialog.LastConfirmMessage));
        cut.WaitForAssertion(
            () => Assert.Equal(expectedDeletes, _handler.Requests.Count(r => r.Method == "DELETE" && r.Url.EndsWith("servers/10/mail/aliases/5", StringComparison.Ordinal))));
    }
}
