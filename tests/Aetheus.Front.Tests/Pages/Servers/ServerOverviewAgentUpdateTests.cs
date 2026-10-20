// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary> When the agent should be updated, the Overview offers "Update" right under its
/// version, to whoever may write on the server, and the click asks the same confirmation as the
/// Tools menu before queuing the update. </summary>
public class ServerOverviewAgentUpdateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerOverviewAgentUpdateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/servers/7/agent/update", new AgentUpdateResponse { ServerId = 7, RequestId = 4, TargetVersion = "1.0.1552" });
    }

    private static ServerDetailDto Server(AgentCompatibilityStatus status, string installed = "1.0.1547",
        string target = "1.0.1552") => new()
        {
            Id = 7,
            Name = "web-prod",
            Hostname = "10.0.0.7",
            IpAddress = "10.0.0.7",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            AgentVersion = installed,
            AgentCompatibility = new AgentCompatibilityDto { Status = status, InstalledVersion = installed, TargetVersion = target },
            Tags = [],
            Services = [],
            Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
            Apache = new ApacheDataDto(),
            Certbot = new CertbotDataDto(),
            Cron = new CronDataDto(),
            Mail = new MailDataDto(),
            Teamspeak = new TeamspeakDataDto(),
            Portsentry = new PortsentryDataDto(),
            Rkhunter = new RkhunterDataDto()
        };

    private IRenderedComponent<ServerOverviewSection> RenderOverview(ServerDetailDto server) =>
        Render<ServerOverviewSection>(parameters => parameters.Add(section => section.Server, server));

    private static AngleSharp.Dom.IElement AgentVersionItem(IRenderedComponent<ServerOverviewSection> cut) =>
        cut.FindAll(".essential-item").Single(item => item.TextContent.Contains("AgentVersion", StringComparison.Ordinal));

    private static AngleSharp.Dom.IElement? UpdateButton(IRenderedComponent<ServerOverviewSection> cut) =>
        AgentVersionItem(cut).QuerySelectorAll("button").SingleOrDefault(button => button.TextContent.Contains("Update", StringComparison.Ordinal));

    private bool UpdateRequested => _handler.Requests.Any(request =>
        request.Method == "POST" && request.Url.Contains("api/servers/7/agent/update", StringComparison.Ordinal));

    [Theory]
    [InlineData(AgentCompatibilityStatus.UpdateRecommended)]
    [InlineData(AgentCompatibilityStatus.UpdateRequired)]
    public void AnUpdateDue_ShowsABlueUpdateButtonUnderTheVersion(AgentCompatibilityStatus status)
    {
        var cut = RenderOverview(Server(status));

        var button = UpdateButton(cut);
        Assert.NotNull(button);
        Assert.Contains("omni-button--primary", button.ClassList);
        Assert.Equal("UpdateAgent", button.GetAttribute("title"));
        Assert.NotNull(button.QuerySelector("svg"));
    }

    [Fact]
    public void AnAgentUpToDate_HasNoUpdateButton()
    {
        var cut = RenderOverview(Server(AgentCompatibilityStatus.UpToDate, installed: "1.0.1552"));

        Assert.Null(UpdateButton(cut));
    }

    [Fact]
    public void ATargetEqualToTheInstalledVersion_HasNoUpdateButton()
    {
        var cut = RenderOverview(Server(AgentCompatibilityStatus.UpdateRecommended, installed: "1.0.1552"));

        Assert.Null(UpdateButton(cut));
    }

    [Fact]
    public void AReader_IsNotOfferedTheUpdate()
    {
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: false);

        var cut = RenderOverview(Server(AgentCompatibilityStatus.UpdateRequired));

        Assert.Null(UpdateButton(cut));
    }

    [Fact]
    public void TheClick_ConfirmsWithBothVersions_ThenQueuesTheUpdate()
    {
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = true;
        var cut = RenderOverview(Server(AgentCompatibilityStatus.UpdateRequired));

        UpdateButton(cut)!.Click();

        cut.WaitForAssertion(() => Assert.True(UpdateRequested));
        Assert.Equal("UpdateAgent", dialog.LastTitle);
        Assert.Equal("Update", dialog.LastConfirmOptions!.OkButtonText);
        Assert.Equal("GoBack", dialog.LastConfirmOptions.CancelButtonText);
    }

    [Fact]
    public async Task ADeclinedConfirmation_QueuesNothing()
    {
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = false;
        var cut = RenderOverview(Server(AgentCompatibilityStatus.UpdateRequired));

        // Awaited: Click() only queues the event when the renderer is busy, and the count was read
        // before the handler ran (Expected 1, Actual 0 under load).
        await UpdateButton(cut)!.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, dialog.OpenCount);
        Assert.False(UpdateRequested);
    }

    // Confirmed updates remain visibly running until a terminal outcome.

    private static AngleSharp.Dom.IElement? UpdateState(IRenderedComponent<ServerOverviewSection> cut) =>
        AgentVersionItem(cut).QuerySelector(".agent-update-overview-state");

    private IRenderedComponent<ServerOverviewSection> ConfirmUpdate(ServerDetailDto server)
    {
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = true;
        var cut = RenderOverview(server);
        UpdateButton(cut)!.Click();
        cut.WaitForAssertion(() => Assert.True(UpdateRequested));
        return cut;
    }

    [Fact]
    public void AfterTheConfirmation_TheButtonIsBusyAndTheTargetVersionIsShown()
    {
        var cut = ConfirmUpdate(Server(AgentCompatibilityStatus.UpdateRequired));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", UpdateButton(cut)!.GetAttribute("aria-busy"));
            Assert.Equal("AgentUpdateInProgressTo", UpdateState(cut)!.TextContent.Trim());
        });
        Assert.Equal("1.0.1552", cut.Instance.AgentUpdateState.InProgressTarget);
    }

    [Fact]
    public void TheNewVersionReported_EndsTheUpdateState()
    {
        var cut = ConfirmUpdate(Server(AgentCompatibilityStatus.UpdateRequired));
        cut.WaitForAssertion(() => Assert.NotNull(UpdateState(cut)));

        cut.Render(parameters => parameters.Add(section => section.Server,
            Server(AgentCompatibilityStatus.UpToDate, installed: "1.0.1552")));

        Assert.Null(UpdateState(cut));
        Assert.Null(UpdateButton(cut));
    }

    [Fact]
    public void AFailedUpdate_ShowsTheFailureAndItsReason_AndTheButtonIsFreeAgain()
    {
        var cut = ConfirmUpdate(Server(AgentCompatibilityStatus.UpdateRequired));
        cut.WaitForAssertion(() => Assert.NotNull(UpdateState(cut)));

        cut.Render(parameters => parameters.Add(section => section.Server, Server(AgentCompatibilityStatus.UpdateRequired) with
        {
            AgentUpdateRequest = new AgentUpdateRequestSummaryDto
            {
                RequestId = 4,
                TargetVersion = "1.0.1552",
                Status = AgentUpdateRequestStatus.Failed,
                FailureDiagnostic = "download refused"
            }
        }));

        var state = UpdateState(cut)!;
        Assert.Equal("alert", state.GetAttribute("role"));
        Assert.Equal("AgentUpdateFailedWithReason", state.TextContent.Trim());
        Assert.Equal("download refused", cut.Instance.AgentUpdateState.FailureDiagnostic);
        Assert.NotEqual("true", UpdateButton(cut)!.GetAttribute("aria-busy"));
    }

    [Fact]
    public void AnOlderFailedRequest_DoesNotEndTheUpdateJustQueued()
    {
        var cut = ConfirmUpdate(Server(AgentCompatibilityStatus.UpdateRequired) with
        {
            AgentUpdateRequest = new AgentUpdateRequestSummaryDto
            {
                RequestId = 3,
                TargetVersion = "1.0.1552",
                Status = AgentUpdateRequestStatus.Failed
            }
        });

        cut.WaitForAssertion(() => Assert.Equal("AgentUpdateInProgressTo", UpdateState(cut)!.TextContent.Trim()));
    }

    [Fact]
    public void ARunningUpdate_ShowsAfterAReload_WithoutAClick()
    {
        var cut = RenderOverview(Server(AgentCompatibilityStatus.UpdateRequired) with
        {
            AgentUpdateRequest = new AgentUpdateRequestSummaryDto
            {
                RequestId = 9,
                TargetVersion = "1.0.1552",
                Status = AgentUpdateRequestStatus.Downloading
            }
        });

        Assert.Equal("AgentUpdateInProgressTo", UpdateState(cut)!.TextContent.Trim());
        Assert.Equal("true", UpdateButton(cut)!.GetAttribute("aria-busy"));
        Assert.False(UpdateRequested);
    }
}
