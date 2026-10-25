// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Drives the TeamSpeak section's toolbar. Each action touches a live voice server with connected
/// users, so it must reach the endpoint of the server on screen, and the ones that are destructive or
/// disruptive must go through a dialog rather than firing on a single click.
/// </summary>
public sealed class ServerTeamspeakSectionActionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServerTeamspeakSectionActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static ServerDetailDto Server(bool installed = true, bool running = true) => new()
    {
        Id = 5,
        Name = "ts-srv",
        Hostname = "10.0.0.5",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = ["teamspeak"],
        Services = [],
        Teamspeak = new TeamspeakDataDto
        {
            IsInstalled = installed,
            IsRunning = running,
            Version = "3.13.7",
            Clients = [],
            Channels = [],
            Bans = []
        }
    };

    private IRenderedComponent<PageHeaderOutletHost> RenderSection(ServerDetailDto? server = null)
    {
        var s = server ?? Server();
        _handler.SetJsonResponse($"api/servers/{s.Id}/teamspeak", s.Teamspeak);
        _handler.SetPaginatedJsonResponse($"api/servers/{s.Id}/teamspeak/clients", Array.Empty<TeamspeakClientDto>());
        _handler.SetPaginatedJsonResponse($"api/servers/{s.Id}/teamspeak/channels", Array.Empty<TeamspeakChannelDto>());
        _handler.SetPaginatedJsonResponse($"api/servers/{s.Id}/teamspeak/bans", Array.Empty<TeamspeakBanDto>());
        return this.RenderWithPageHeader<ServerTeamspeakSection>(p => p
            .Add(x => x.Server, s)
            .Add(x => x.ServerId, s.Id));
    }

    // R3-42: only Restart is a button of the bar; the other actions are entries of the "More actions" menu,
    // which this opens when no button carries the name.
    private static IElement? TryButton(IRenderedComponent<PageHeaderOutletHost> cut, string label) =>
        cut.FindAll("button:not([role='menuitem'])").FirstOrDefault(b => b.Names().Contains(label, StringComparison.Ordinal))
        ?? cut.FindMenuItem(label);

    [Fact]
    public void AnInstalledServerOffersItsManagementToolbar()
    {
        var cut = RenderSection(Server(installed: true));

        Assert.NotNull(TryButton(cut, "EditServer"));
        Assert.NotNull(TryButton(cut, "SendMessage"));
        Assert.NotNull(TryButton(cut, "GracefulRestart"));
        Assert.NotNull(TryButton(cut, "SnapshotCreate"));
    }

    [Fact]
    public void TheBarHasOneButton_Restart_AndEveryOtherActionIsInTheSingleMoreActionsMenu()
    {
        // Recette R3-41/R3-42: never several action buttons in a row.
        var cut = RenderSection();

        // Recette R-29: the bar is the page header now; the status row keeps only the badges.
        Assert.Empty(cut.Find(".omni-row").QuerySelectorAll("button"));
        var barButtons = cut.FindAll(".test-outlet > .omni-button");
        var restart = Assert.Single(barButtons);
        Assert.Contains("Restart", restart.TextContent, StringComparison.Ordinal);
        Assert.Contains("omni-button--primary", restart.ClassName, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".test-outlet > .omni-overflow-menu"));

        cut.Find(".test-outlet > .omni-overflow-menu .omni-overflow-menu__trigger").Click();
        var entries = cut.FindAll(".omni-overflow-menu__popup .omni-menu__item").Select(e => e.TextContent.Trim()).ToList();
        string[] expected =
            ["Start", "Stop", "EditServer", "Send", "CopyCredentials", "GracefulRestart", "TeamspeakStats", "Snapshot",
             "SnapshotDeploy", "Groups", "TeamspeakTokens", "TeamspeakComplaints", "Connect"];
        Assert.Equal(expected, entries);
    }

    [Fact]
    public void R3_42_StopInTheMoreActionsMenu_IsRed_LikeApacheAndDocker()
    {
        var cut = RenderSection();

        cut.Find(".test-outlet > .omni-overflow-menu .omni-overflow-menu__trigger").Click();
        var items = cut.FindAll(".omni-overflow-menu__popup .omni-menu__item");
        var stop = items.Single(e => e.TextContent.Trim() == "Stop");
        Assert.Contains("omni-menu__item--danger", stop.ClassName, StringComparison.Ordinal);
        var start = items.Single(e => e.TextContent.Trim() == "Start");
        Assert.DoesNotContain("omni-menu__item--danger", start.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void AMovedActionStillRunsItsHandler_StopPostsTheStopAction()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/5/teamspeak/action", new { success = true });
        var cut = RenderSection();

        cut.FindMenuItem("Stop")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.Contains("teamspeak/action", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARowOfClientsKeepsInfoVisible_AndPutsMoveAndPokeAndKickAndBanInARowMenu()
    {
        var server = Server();
        _handler.SetJsonResponse($"api/servers/{server.Id}/teamspeak", server.Teamspeak);
        _handler.SetPaginatedJsonResponse($"api/servers/{server.Id}/teamspeak/clients",
            new[] { new TeamspeakClientDto { ClientId = 7, Nickname = "Alice", ChannelId = 1, Platform = "Windows" } });
        _handler.SetPaginatedJsonResponse($"api/servers/{server.Id}/teamspeak/channels", Array.Empty<TeamspeakChannelDto>());
        _handler.SetPaginatedJsonResponse($"api/servers/{server.Id}/teamspeak/bans", Array.Empty<TeamspeakBanDto>());
        var cut = this.RenderWithPageHeader<ServerTeamspeakSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));

        cut.WaitForState(() => cut.FindAll("tbody .omni-overflow-menu__trigger").Count > 0);
        var cell = cut.Find("tbody .omni-overflow-menu__trigger").ParentElement!.ParentElement!;
        Assert.Single(cell.QuerySelectorAll(".omni-button[title='ClientInfo']"));
        Assert.Empty(cell.QuerySelectorAll(".omni-button[title='Kick'], .omni-button[title='Ban'], .omni-button[title='Move'], .omni-button[title='Poke']"));

        cut.Find("tbody .omni-overflow-menu__trigger").Click();
        var items = cut.FindAll(".omni-overflow-menu__popup .omni-menu__item");
        Assert.Equal(["Move", "Poke", "Kick", "Ban"], items.Select(i => i.TextContent.Trim()).ToArray());
        Assert.All(items.Where(i => i.TextContent.Trim() is "Kick" or "Ban"),
            i => Assert.Contains("danger", i.ClassName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnUninstalledServerOffersSetupAndNotTheManagementToolbar()
    {
        // Editing a virtual server that does not exist can only fail at the agent.
        var cut = RenderSection(Server(installed: false, running: false));

        Assert.NotNull(TryButton(cut, "TeamspeakSetup"));
        Assert.Null(TryButton(cut, "EditServer"));
        Assert.Null(TryButton(cut, "GracefulRestart"));
    }

    [Fact]
    public void TheQuickConnectLinkPointsAtTheServersOwnHost()
    {
        // A wrong host here sends the operator's TeamSpeak client to somebody else's server.
        var cut = RenderSection();

        var link = cut.FindMenuItem("Connect")!;
        Assert.Contains("10.0.0.5", link.GetAttribute("title") ?? string.Empty, StringComparison.Ordinal);
        Assert.StartsWith("ts3server://", link.GetAttribute("title") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSectionLoadsItsOwnServersTeamspeakData()
    {
        // Loading another server id here would show someone else's voice server. The per-tab grids
        // (clients, channels, bans) load lazily when their tab is opened, so only the section's own
        // payload is fetched on render.
        var cut = RenderSection();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Url.Contains("servers/5/teamspeak", StringComparison.Ordinal)));
        Assert.DoesNotContain(_handler.Requests, r =>
            r.Url.Contains("servers/6/", StringComparison.Ordinal));
    }

    [Fact]
    public void AGracefulRestartAsksBeforeDisconnectingAnyone()
    {
        // It drops every connected client; firing it on one click would be a trap.
        _dialog.OpenResult = null;
        var cut = RenderSection();
        // Count writes only: a late GET from the async load would otherwise fail this for the wrong reason.
        var before = _handler.Requests.Count(r => r.Method == "POST");

        TryButton(cut, "GracefulRestart")!.Click();

        cut.WaitForAssertion(() => Assert.True(_dialog.OpenCount > 0));
        Assert.Equal(before, _handler.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public void SendingAMessageGoesThroughADialogRatherThanFiringImmediately()
    {
        // A broadcast reaches every connected user; there is no undo.
        _dialog.OpenResult = null;
        var cut = RenderSection();
        // Count writes only: a late GET from the async load would otherwise fail this for the wrong reason.
        var before = _handler.Requests.Count(r => r.Method == "POST");

        TryButton(cut, "SendMessage")!.Click();

        cut.WaitForAssertion(() => Assert.True(_dialog.OpenCount > 0));
        Assert.Equal(before, _handler.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public void EditingTheVirtualServerOpensADialogAndCallsNothingUntilConfirmed()
    {
        _dialog.OpenResult = null;
        var cut = RenderSection();
        // Count writes only: a late GET from the async load would otherwise fail this for the wrong reason.
        var before = _handler.Requests.Count(r => r.Method == "POST");

        TryButton(cut, "EditServer")!.Click();

        cut.WaitForAssertion(() => Assert.True(_dialog.OpenCount > 0));
        Assert.Equal(before, _handler.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public void DeployingASnapshotIsGatedByADialog()
    {
        // Deploying a snapshot overwrites the live virtual server's configuration.
        _dialog.OpenResult = null;
        var cut = RenderSection();
        // Count writes only: a late GET from the async load would otherwise fail this for the wrong reason.
        var before = _handler.Requests.Count(r => r.Method == "POST");

        TryButton(cut, "SnapshotDeploy")!.Click();

        cut.WaitForAssertion(() => Assert.True(_dialog.OpenCount > 0));
        Assert.Equal(before, _handler.Requests.Count(r => r.Method == "POST"));
    }

    // --- confirmed dialogs: the handler continues past the dialog and calls the API ---------------

    private TeamspeakDialogModel Confirmed(TeamspeakDialogMode mode) => new()
    {
        Mode = mode,
        Message = "maintenance in 5 minutes",
        WarningSeconds = 30,
        SnapshotBlob = "snapshot-payload",
        Confirmation = "ts-srv",
        ExpectedConfirmation = "ts-srv"
    };

    [Fact]
    public void ConfirmingAGracefulRestart_PostsToTheGracefulRestartEndpoint()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/servers/5/teamspeak/graceful-restart", new { success = true });
        _dialog.OpenResult = Confirmed(TeamspeakDialogMode.GracefulRestart);
        var cut = RenderSection();

        TryButton(cut, "GracefulRestart")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.Contains("teamspeak/graceful-restart", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConfirmingABroadcast_PostsToTheMessageEndpoint()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/5/teamspeak/message", new { success = true });
        _dialog.OpenResult = Confirmed(TeamspeakDialogMode.Message);
        var cut = RenderSection();

        TryButton(cut, "SendMessage")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.Contains("teamspeak/message", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConfirmingASnapshotDeploy_PostsToTheSnapshotsEndpoint()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/5/teamspeak/snapshots", new { success = true });
        _dialog.OpenResult = Confirmed(TeamspeakDialogMode.SnapshotDeploy);
        var cut = RenderSection();

        TryButton(cut, "SnapshotDeploy")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.Contains("teamspeak/snapshots", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EachConfirmedActionTargetsItsOwnEndpoint_NotASharedOne()
    {
        // A single generic endpoint would mean the dialog choice does not decide what happens.
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/servers/5/teamspeak/graceful-restart", new { success = true });
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/5/teamspeak/message", new { success = true });

        _dialog.OpenResult = Confirmed(TeamspeakDialogMode.GracefulRestart);
        var restart = RenderSection();
        TryButton(restart, "GracefulRestart")!.Click();
        restart.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Url.Contains("graceful-restart", StringComparison.Ordinal)));

        // One page header per page: the first page leaves before the second renders its own actions.
        await DisposeComponentsAsync();
        _dialog.OpenResult = Confirmed(TeamspeakDialogMode.Message);
        var message = RenderSection();
        TryButton(message, "SendMessage")!.Click();
        message.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Url.Contains("teamspeak/message", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConfirmingSetupOnAnUninstalledServer_PostsToTheSetupEndpoint()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/5/teamspeak/setup", new { success = true });
        _dialog.OpenResult = Confirmed(TeamspeakDialogMode.Setup);
        var cut = RenderSection(Server(installed: false, running: false));

        TryButton(cut, "TeamspeakSetup")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.Contains("teamspeak/setup", StringComparison.Ordinal)));
    }
}
