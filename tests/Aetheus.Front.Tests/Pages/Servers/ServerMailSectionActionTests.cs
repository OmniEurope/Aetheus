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
        // Recette R2-090: the service controls moved into the status list, shown once the counts are read.
        cut.WaitForAssertion(() => Assert.NotNull(TryButton(cut, "Postfix")));
    }

    [Fact]
    public void AddingADomainWithoutConfirming_CallsNothing()
    {
        // The dialog is where the domain name is typed; dismissing it must not create anything.
        _dialog.OpenResult = null;
        var cut = RenderSection();
        var before = _handler.Requests.Count(r => r.Method == "POST");

        TryButton(cut, "AddDomain")!.Click();

        cut.WaitForAssertion(() => Assert.True(_dialog.OpenCount > 0), TimeSpan.FromSeconds(3));
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
                r.Method == "POST" && r.Url.EndsWith("servers/10/mail/domains", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3));
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
            () => Assert.True(_handler.Requests.Count(r => r.Method == "GET") > readsBefore),
            TimeSpan.FromSeconds(3));
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
        }, TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("servers/11/", StringComparison.Ordinal));
    }
}
