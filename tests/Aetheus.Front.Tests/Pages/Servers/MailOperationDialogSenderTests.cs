// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R3-47: the sender of the delivery test is one of the accounts of the server, the administration account first.</summary>
public sealed class MailOperationDialogSenderTests : BunitContext
{
    public MailOperationDialogSenderTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static MailAccountDto Account(string email, bool active = true) => new() { Email = email, IsActive = active };

    private IRenderedComponent<MailOperationDialog> RenderTest(string defaultSender, params MailAccountDto[] accounts)
    {
        var cut = Render<MailOperationDialog>(p => p
            .Add(x => x.Mode, MailDialogMode.TestDelivery)
            .Add(x => x.Model, new MailDialogModel { Source = defaultSender })
            .Add(x => x.Accounts, accounts));
        BunitTestHelper.Interactive(this);
        return cut;
    }

    [Fact]
    public void R3_47_TheSender_IsADropDownOfTheServerAccounts_PreselectedOnTheAdministrationAccount()
    {
        var cut = RenderTest("alice@example.com", Account("alice@example.com"), Account("admin@example.com"), Account("zed@example.com"));

        var select = cut.Find("select");
        Assert.Equal(["alice@example.com", "admin@example.com", "zed@example.com"],
            select.QuerySelectorAll("option").Select(o => o.TextContent.Trim()).ToArray());
        Assert.Equal("admin@example.com", select.QuerySelectorAll("option").Single(o => o.HasAttribute("selected")).TextContent.Trim());
        Assert.Empty(cut.FindAll("input[placeholder='admin@example.com']"));
    }

    [Fact]
    public void R3_47_WithoutAdministrationAccount_TheFirstActiveAccountIsPreselected()
    {
        var cut = RenderTest(string.Empty, Account("gone@example.com", active: false), Account("bob@example.com"), Account("carol@example.com"));

        var selected = cut.Find("select").QuerySelectorAll("option").Single(o => o.HasAttribute("selected"));
        Assert.Equal("bob@example.com", selected.TextContent.Trim());
        Assert.DoesNotContain("gone@example.com", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_47_WhenNoAccountIsKnown_TheSenderStaysATypedAddress()
    {
        var cut = RenderTest("typed@example.com");

        Assert.Empty(cut.FindAll("select"));
        Assert.Equal("typed@example.com", cut.Find("input[placeholder='admin@example.com']").GetAttribute("value"));
    }

    private const string RecipientInput = "#oe-formfield-pages-servers-serverdetailsections-mailoperationdialog-22";

    private ImmediateDialogService DialogService => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private IReadOnlyList<string> OpenRecipientSuggestions(IRenderedComponent<MailOperationDialog> cut)
    {
        cut.Find("[data-omni-autocomplete-toggle]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".omni-autocomplete__results li")));
        return [.. cut.FindAll(".omni-autocomplete__results li").Select(option => option.TextContent.Trim())];
    }

    [Fact]
    public void R3_47_TheSenderAndTheRecipient_AreStarredAsRequired()
    {
        var cut = RenderTest(string.Empty, Account("admin@example.com"));

        var required = cut.FindAll("label.omni-label--required").Select(label => label.TextContent).ToList();
        Assert.Contains(required, text => text.Contains("Sender", StringComparison.Ordinal));
        Assert.Contains(required, text => text.Contains("Recipient", StringComparison.Ordinal));
    }

    [Fact]
    public void R3_47_TheRecipient_SuggestsTheActiveAccountsOfTheServer()
    {
        var cut = RenderTest(string.Empty, Account("admin@example.com"), Account("gone@example.com", active: false), Account("bob@example.com"));

        Assert.Equal(["admin@example.com", "bob@example.com"], OpenRecipientSuggestions(cut));
    }

    [Fact]
    public void R3_47_TheRecipientSuggestions_FollowTheTypedText()
    {
        var cut = RenderTest(string.Empty, Account("admin@example.com"), Account("bob@example.com"));

        cut.Find(RecipientInput).Input("BOB");

        cut.WaitForAssertion(() => Assert.Equal(["bob@example.com"],
            cut.FindAll(".omni-autocomplete__results li").Select(option => option.TextContent.Trim()).ToArray()));
    }

    [Fact]
    public void R3_47_APickedRecipientSuggestion_IsSentAsTheRecipient()
    {
        var cut = RenderTest(string.Empty, Account("admin@example.com"), Account("bob@example.com"));

        OpenRecipientSuggestions(cut);
        cut.FindAll(".omni-autocomplete__results li").First(option => option.TextContent.Trim() == "bob@example.com").Click();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(1, DialogService.CloseCount));
        var sent = Assert.IsType<MailDialogModel>(DialogService.LastCloseResult);
        Assert.Equal("admin@example.com", sent.Source);
        Assert.Equal("bob@example.com", sent.Destination);
    }

    [Fact]
    public void R3_47_AnAddressThatIsNotAnAccount_IsSentAsTyped()
    {
        var cut = RenderTest(string.Empty, Account("admin@example.com"));

        var input = cut.Find(RecipientInput);
        input.Input("someone@elsewhere.org");
        input.Blur();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(1, DialogService.CloseCount));
        Assert.Equal("someone@elsewhere.org", Assert.IsType<MailDialogModel>(DialogService.LastCloseResult).Destination);
    }

    [Fact]
    public void R3_47_WithoutRecipient_TheTestIsNotSent()
    {
        var cut = RenderTest(string.Empty, Account("admin@example.com"));

        cut.Find("form").Submit();

        Assert.Equal(0, DialogService.CloseCount);
    }
}
