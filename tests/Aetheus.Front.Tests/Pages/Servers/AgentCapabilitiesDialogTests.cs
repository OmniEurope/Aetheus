// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R3-58 (ADR-051), R1-5 (ADR-055): the dialog that updates an agent and installs the whitelisted
/// components chosen (one card each), in place of the installer's flag run over SSH. Nothing chosen means
/// the agent alone is updated.</summary>
public sealed class AgentCapabilitiesDialogTests : BunitContext
{
    private const string EndpointFragment = "api/servers/5/agent/enable-capabilities";
    private const string UpdateFragment = "api/servers/5/agent/update";
    private readonly BunitTestHelper.TestHandler _handler;

    public AgentCapabilitiesDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private ImmediateDialogService Dialogs => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private static ServerDetailDto Server(bool agentCanEnable = true, bool? patchGranted = false, string[]? reported = null) => new()
    {
        Id = 5,
        Name = "vps-1",
        Hostname = "10.0.0.5",
        Status = ServerStatus.Online,
        LastHeartbeat = DateTime.Now,
        OsDescription = "Debian GNU/Linux 12",
        Tags = [],
        PatchManagementAvailable = patchGranted,
        AgentCapabilities = agentCanEnable
            ? [AgentCapabilities.SelfUpdate, AgentCapabilities.EnableCapabilitiesOnUpdate, .. reported ?? []]
            : [AgentCapabilities.SelfUpdate]
    };

    private IRenderedComponent<AgentCapabilitiesDialog> RenderDialog(ServerDetailDto? server = null) =>
        Render<AgentCapabilitiesDialog>(p => p.Add(x => x.Server, server ?? Server()));

    private static IReadOnlyList<AngleSharp.Dom.IElement> Cards(IRenderedComponent<AgentCapabilitiesDialog> cut) =>
        cut.FindAll("[role=\"checkbox\"]");

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<AgentCapabilitiesDialog> cut, string token) =>
        Cards(cut)[AgentEnableableCapabilities.Whitelist.ToList().IndexOf(token)];

    private static AngleSharp.Dom.IElement Submit(IRenderedComponent<AgentCapabilitiesDialog> cut) =>
        cut.Find("button.agent-capabilities-submit");

    private static string?[] SentCapabilities(BunitTestHelper.TestHandler handler)
    {
        var sent = Assert.Single(handler.RequestDetails, request => request.Url.Contains(EndpointFragment, StringComparison.Ordinal));
        Assert.Equal("POST", sent.Method);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.True(body.RootElement.TryGetProperty("capabilities", out var capabilities));
        return [.. capabilities.EnumerateArray().Select(item => item.GetString())];
    }

    [Fact]
    public void TheCapabilityField_HasAHelpMark_AndOffersOneCardPerWhitelistedComponent()
    {
        var cut = RenderDialog();

        Assert.NotEmpty(cut.FindAll("[aria-describedby], [aria-label=\"EnableAgentCapabilitiesHelp\"], .omni-form-field__help"));
        Assert.Equal(AgentEnableableCapabilities.Whitelist.Count, Cards(cut).Count);
        Assert.Equal(AgentEnableableCapabilities.Whitelist, AgentCapabilitiesDialog.Options.Select(option => option.Token));
        Assert.Contains("WizardModulePatchManagement", Card(cut, AgentEnableableCapabilities.PatchManage).TextContent, StringComparison.Ordinal);
        Assert.Contains("AgentComponentMailSetup", Card(cut, AgentEnableableCapabilities.MailSetup).TextContent, StringComparison.Ordinal);
        Assert.Contains("AgentComponentTeamspeakSetup", Card(cut, AgentEnableableCapabilities.TeamspeakSetup).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCardsNeverOfferWhatStaysOnTheInstaller()
    {
        var text = RenderDialog().Markup;

        Assert.DoesNotContain("service-control", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enable-all", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingChosen_ThePrimaryActionOnlyUpdatesTheAgent_ThenOffersTheEnableLabelOnceAComponentIsChosen()
    {
        var cut = RenderDialog();

        Assert.False(Submit(cut).HasAttribute("disabled"));
        Assert.Contains("EnableAgentCapabilitiesSubmitUpdate", Submit(cut).TextContent, StringComparison.Ordinal);

        Card(cut, AgentEnableableCapabilities.CronManage).Click();

        Assert.False(Submit(cut).HasAttribute("disabled"));
        Assert.Contains("EnableAgentCapabilitiesSubmit", Submit(cut).TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableAgentCapabilitiesSubmitUpdate", Submit(cut).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Submitting_NothingChosen_QueuesThePlainAgentUpdate_AndAsksForNoCapability()
    {
        _handler.SetJsonResponse(HttpMethod.Post, UpdateFragment, new AgentUpdateResponse { ServerId = 5, RequestId = 12 });
        var cut = RenderDialog();

        Submit(cut).Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Dialogs.CloseCount));
        Assert.Contains(_handler.RequestsSnapshot(),
            request => request.Method == "POST" && request.Url.Contains(UpdateFragment, StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.RequestsSnapshot(),
            request => request.Url.Contains(EndpointFragment, StringComparison.Ordinal));
        Assert.Equal(12, Assert.IsType<AgentUpdateResponse>(Dialogs.LastCloseResult).RequestId);
    }

    [Fact]
    public void Submitting_PostsOnlyTheChosenWhitelistedToken_AndClosesWithTheQueuedUpdate()
    {
        _handler.SetJsonResponse(HttpMethod.Post, EndpointFragment, new AgentUpdateResponse { ServerId = 5, RequestId = 11 });
        var cut = RenderDialog();
        Card(cut, AgentEnableableCapabilities.PatchManage).Click();

        Submit(cut).Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Dialogs.CloseCount));
        Assert.Equal(["patch-manage"], SentCapabilities(_handler));
        Assert.Equal(11, Assert.IsType<AgentUpdateResponse>(Dialogs.LastCloseResult).RequestId);
    }

    [Theory]
    [InlineData("package-manage")]
    [InlineData("service-enable")]
    [InlineData("cron-manage")]
    [InlineData("rkhunter-manage")]
    [InlineData("apache-manage")]
    [InlineData("certbot-manage")]
    [InlineData("portsentry-manage")]
    [InlineData("firewall-manage")]
    [InlineData("mail-setup")]
    [InlineData("teamspeak-setup")]
    public void EachComponentCard_PostsItsOwnTokenAlone(string token)
    {
        _handler.SetJsonResponse(HttpMethod.Post, EndpointFragment, new AgentUpdateResponse { ServerId = 5, RequestId = 11 });
        var cut = RenderDialog();
        Card(cut, token).Click();

        Submit(cut).Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Dialogs.CloseCount));
        Assert.Equal([token], SentCapabilities(_handler));
    }

    [Fact]
    public void SeveralCards_PostTheirTokensInWhitelistOrder()
    {
        _handler.SetJsonResponse(HttpMethod.Post, EndpointFragment, new AgentUpdateResponse { ServerId = 5, RequestId = 11 });
        var cut = RenderDialog();
        Card(cut, AgentEnableableCapabilities.MailSetup).Click();
        Card(cut, AgentEnableableCapabilities.PatchManage).Click();

        Submit(cut).Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Dialogs.CloseCount));
        var sent = SentCapabilities(_handler);
        Assert.Equal(2, sent.Length);
        Assert.Contains("patch-manage", sent);
        Assert.Contains("mail-setup", sent);
        Assert.All(sent, token => Assert.Contains(token!, AgentEnableableCapabilities.Whitelist));
    }

    [Fact]
    public void ARefusedRequest_KeepsTheDialogOpen()
    {
        _handler.SetResponse(EndpointFragment, HttpStatusCode.Conflict);
        var cut = RenderDialog();
        Card(cut, AgentEnableableCapabilities.PatchManage).Click();

        Submit(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(),
            request => request.Method == "POST" && request.Url.Contains(EndpointFragment, StringComparison.Ordinal)));
        Assert.Equal(0, Dialogs.CloseCount);
    }

    [Fact]
    public void AnAgentThatDoesNotDeclareTheField_IsToldToUpdateFirst_CannotChooseAComponent_AndOnlyUpdates()
    {
        _handler.SetJsonResponse(HttpMethod.Post, UpdateFragment, new AgentUpdateResponse { ServerId = 5, RequestId = 13 });
        var cut = RenderDialog(Server(agentCanEnable: false));

        Assert.NotEmpty(cut.FindAll(".agent-capabilities-agent-old"));
        Assert.Contains("EnableAgentCapabilitiesAgentOld", cut.Find(".agent-capabilities-agent-old").TextContent, StringComparison.Ordinal);
        Card(cut, AgentEnableableCapabilities.PatchManage).Click();
        Assert.Contains("EnableAgentCapabilitiesSubmitUpdate", Submit(cut).TextContent, StringComparison.Ordinal);

        Submit(cut).Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Dialogs.CloseCount));
        Assert.DoesNotContain(_handler.RequestsSnapshot(),
            request => request.Url.Contains(EndpointFragment, StringComparison.Ordinal));
        Assert.Contains(_handler.RequestsSnapshot(),
            request => request.Method == "POST" && request.Url.Contains(UpdateFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void ACapabilityAlreadyGranted_IsShownAsSuch_AndCannotBeChosen()
    {
        var cut = RenderDialog(Server(patchGranted: true));

        var card = Card(cut, AgentEnableableCapabilities.PatchManage);
        Assert.Contains("EnableAgentCapabilitiesAlreadyGranted", card.TextContent, StringComparison.Ordinal);
        card.Click();
        Assert.Contains("EnableAgentCapabilitiesSubmitUpdate", Submit(cut).TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package-manage", "package.manage")]
    [InlineData("service-enable", "service.manage")]
    [InlineData("cron-manage", "cron.manage")]
    [InlineData("apache-manage", "apache.manage")]
    [InlineData("certbot-manage", "certbot.manage")]
    [InlineData("portsentry-manage", "portsentry.manage")]
    [InlineData("firewall-manage", "firewall.manage")]
    [InlineData("mail-setup", "mail.manage")]
    [InlineData("teamspeak-setup", "teamspeak.manage")]
    public void AComponentTheAgentReports_IsShownAsAlreadyEnabled_AndTheOthersAreNot(string token, string reported)
    {
        var cut = RenderDialog(Server(reported: [reported]));

        Assert.Contains("EnableAgentCapabilitiesAlreadyGranted", Card(cut, token).TextContent, StringComparison.Ordinal);
        var granted = Cards(cut).Count(card => card.TextContent.Contains("EnableAgentCapabilitiesAlreadyGranted", StringComparison.Ordinal));
        Assert.Equal(1, granted);
    }

    [Fact]
    public void Rkhunter_ReportsNoCapability_SoItIsAlwaysOffered()
    {
        var cut = RenderDialog(Server(reported: ["package.manage", "cron.manage"]));

        Assert.DoesNotContain("EnableAgentCapabilitiesAlreadyGranted", Card(cut, AgentEnableableCapabilities.RkhunterManage).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_ClosesWithoutRequest()
    {
        var cut = RenderDialog();

        cut.FindAll("button").First(button => button.TextContent.Contains("GoBack", StringComparison.Ordinal)).Click();

        Assert.Equal(1, Dialogs.CloseCount);
        Assert.Null(Dialogs.LastCloseResult);
        Assert.DoesNotContain(_handler.RequestsSnapshot(),
            request => request.Url.Contains(EndpointFragment, StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.RequestsSnapshot(),
            request => request.Url.Contains(UpdateFragment, StringComparison.Ordinal));
    }
}
