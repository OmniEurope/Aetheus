// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R3-58 (ADR-051): the dialog that updates an agent and grants it the whitelisted capability
/// chosen, in place of the installer's flag run over SSH.</summary>
public sealed class AgentCapabilitiesDialogTests : BunitContext
{
    private const string EndpointFragment = "api/servers/5/agent/enable-capabilities";
    private readonly BunitTestHelper.TestHandler _handler;

    public AgentCapabilitiesDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private ImmediateDialogService Dialogs => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private static ServerDetailDto Server(bool agentCanEnable = true, bool? patchGranted = false) => new()
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
            ? [AgentCapabilities.SelfUpdate, AgentCapabilities.EnableCapabilitiesOnUpdate]
            : [AgentCapabilities.SelfUpdate]
    };

    private IRenderedComponent<AgentCapabilitiesDialog> RenderDialog(ServerDetailDto? server = null) =>
        Render<AgentCapabilitiesDialog>(p => p.Add(x => x.Server, server ?? Server()));

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<AgentCapabilitiesDialog> cut) =>
        cut.Find("[role=\"checkbox\"]");

    private static AngleSharp.Dom.IElement Submit(IRenderedComponent<AgentCapabilitiesDialog> cut) =>
        cut.Find("button.agent-capabilities-submit");

    [Fact]
    public void TheCapabilityField_IsStarredAsRequired_WithAHelpMark_AndOffersOnlyPatchManage()
    {
        var cut = RenderDialog();

        Assert.Contains(cut.FindAll("label.omni-label--required"),
            label => label.TextContent.Contains("EnableAgentCapabilitiesField", StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll("[aria-describedby], [aria-label=\"EnableAgentCapabilitiesHelp\"], .omni-form-field__help"));
        var cards = cut.FindAll("[role=\"checkbox\"]");
        Assert.Single(cards);
        Assert.Contains("WizardModulePatchManagement", cards[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingChosen_TheOnePrimaryActionIsDisabled_ThenEnabledByTheCard()
    {
        var cut = RenderDialog();

        Assert.True(Submit(cut).HasAttribute("disabled"));

        Card(cut).Click();

        Assert.False(Submit(cut).HasAttribute("disabled"));
        Assert.Contains("EnableAgentCapabilitiesSubmit", Submit(cut).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Submitting_PostsOnlyTheChosenWhitelistedToken_AndClosesWithTheQueuedUpdate()
    {
        _handler.SetJsonResponse(HttpMethod.Post, EndpointFragment, new AgentUpdateResponse { ServerId = 5, RequestId = 11 });
        var cut = RenderDialog();
        Card(cut).Click();

        Submit(cut).Click();

        cut.WaitForAssertion(() => Assert.Equal(1, Dialogs.CloseCount));
        var sent = Assert.Single(_handler.RequestDetails, request => request.Url.Contains(EndpointFragment, StringComparison.Ordinal));
        Assert.Equal("POST", sent.Method);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.True(body.RootElement.TryGetProperty("capabilities", out var capabilities));
        Assert.Equal(["patch-manage"], capabilities.EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(11, Assert.IsType<AgentUpdateResponse>(Dialogs.LastCloseResult).RequestId);
    }

    [Fact]
    public void ARefusedRequest_KeepsTheDialogOpen()
    {
        _handler.SetResponse(EndpointFragment, HttpStatusCode.Conflict);
        var cut = RenderDialog();
        Card(cut).Click();

        Submit(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(),
            request => request.Method == "POST" && request.Url.Contains(EndpointFragment, StringComparison.Ordinal)));
        Assert.Equal(0, Dialogs.CloseCount);
    }

    [Fact]
    public void AnAgentThatDoesNotDeclareTheField_IsToldToUpdateFirst_AndCannotSubmit()
    {
        var cut = RenderDialog(Server(agentCanEnable: false));

        Assert.NotEmpty(cut.FindAll(".agent-capabilities-agent-old"));
        Assert.Contains("EnableAgentCapabilitiesAgentOld", cut.Find(".agent-capabilities-agent-old").TextContent, StringComparison.Ordinal);
        Card(cut).Click();
        Assert.True(Submit(cut).HasAttribute("disabled"));
        Assert.DoesNotContain(_handler.RequestsSnapshot(),
            request => request.Url.Contains(EndpointFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void ACapabilityAlreadyGranted_IsShownAsSuch_AndCannotBeChosen()
    {
        var cut = RenderDialog(Server(patchGranted: true));

        Assert.Contains("EnableAgentCapabilitiesAlreadyGranted", Card(cut).TextContent, StringComparison.Ordinal);
        Card(cut).Click();
        Assert.True(Submit(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Cancel_ClosesWithoutRequest()
    {
        var cut = RenderDialog();

        cut.FindAll("button").First(button => button.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();

        Assert.Equal(1, Dialogs.CloseCount);
        Assert.Null(Dialogs.LastCloseResult);
        Assert.DoesNotContain(_handler.RequestsSnapshot(),
            request => request.Url.Contains(EndpointFragment, StringComparison.Ordinal));
    }
}
