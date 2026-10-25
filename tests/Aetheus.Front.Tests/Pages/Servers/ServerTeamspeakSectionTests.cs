// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerTeamspeakSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerTeamspeakSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeTsServer(bool installed = true, bool running = true) => new()
    {
        Id = 5,
        Name = "ts-srv",
        Hostname = "10.0.0.5",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        CpuPercent = 10,
        MemoryUsedMb = 2048,
        MemoryTotalMb = 4096,
        DiskUsedGb = 20,
        DiskTotalGb = 100,
        Tags = ["teamspeak"],
        Services = [],
        Teamspeak = new TeamspeakDataDto
        {
            IsInstalled = installed,
            IsRunning = running,
            Version = installed ? "3.13.7" : string.Empty,
            ServerName = installed ? "My TS Server" : string.Empty,
            VoicePort = 9987,
            QueryPort = 10011,
            MaxClients = 32,
            OnlineClients = 5,
            ChannelCount = 3,
            UptimeSeconds = 7200,
            Channels =
            [
                new TeamspeakChannelDto { Id = 1, Name = "Default", ParentId = 0, TotalClients = 3, MaxClients = -1, IsDefault = true, IsPermanent = true },
                new TeamspeakChannelDto { Id = 2, Name = "AFK Room", ParentId = 0, TotalClients = 1, MaxClients = 10, HasPassword = true, IsPermanent = true },
                new TeamspeakChannelDto { Id = 3, Name = "Temp Gaming", ParentId = 1, TotalClients = 1, MaxClients = 5, IsPermanent = false }
            ],
            Clients =
            [
                new TeamspeakClientDto { ClientId = 1, Nickname = "Player1", ChannelId = 1, ChannelName = "Default", Platform = "Windows", UniqueId = "uid1" },
                new TeamspeakClientDto { ClientId = 2, Nickname = "Player2", ChannelId = 2, ChannelName = "AFK Room", Platform = "Linux", UniqueId = "uid2" },
                new TeamspeakClientDto { ClientId = 99, Nickname = "serveradmin", ChannelId = 1, IsServerQuery = true, UniqueId = "serverquery" }
            ]
        }
    };

    private IRenderedComponent<ServerTeamspeakSection> RenderSection(ServerDetailDto? server = null)
    {
        var s = server ?? MakeTsServer();
        _handler.SetJsonResponse($"api/servers/{s.Id}/teamspeak",
            s.Teamspeak with { Clients = [], Channels = [], Bans = [] });
        _handler.SetPaginatedJsonResponse($"api/servers/{s.Id}/teamspeak/clients",
            s.Teamspeak.Clients.Where(client => !client.IsServerQuery));
        _handler.SetPaginatedJsonResponse($"api/servers/{s.Id}/teamspeak/channels", s.Teamspeak.Channels);
        _handler.SetPaginatedJsonResponse($"api/servers/{s.Id}/teamspeak/bans", s.Teamspeak.Bans);
        return Render<ServerTeamspeakSection>(p => p
            .Add(x => x.Server, s)
            .Add(x => x.ServerId, s.Id));
    }

    // Recette R-29: Setup and Restart sit in the page header, rendered here with the section.
    private IRenderedComponent<PageHeaderOutletHost> RenderSectionWithHeader(ServerDetailDto server)
    {
        _handler.SetJsonResponse($"api/servers/{server.Id}/teamspeak",
            server.Teamspeak with { Clients = [], Channels = [], Bans = [] });
        _handler.SetPaginatedJsonResponse($"api/servers/{server.Id}/teamspeak/clients", Array.Empty<TeamspeakClientDto>());
        _handler.SetPaginatedJsonResponse($"api/servers/{server.Id}/teamspeak/channels", Array.Empty<TeamspeakChannelDto>());
        _handler.SetPaginatedJsonResponse($"api/servers/{server.Id}/teamspeak/bans", Array.Empty<TeamspeakBanDto>());
        return this.RenderWithPageHeader<ServerTeamspeakSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, server.Id));
    }

    // --- Rendering ---

    [Fact]
    public void Renders_InstalledState_ShowsStatusBadges()
    {
        var cut = RenderSection();
        var markup = cut.Markup;

        Assert.Contains("Installed", markup);
        Assert.Contains("Running", markup);
        Assert.Contains("3.13.7", markup);
    }

    [Fact]
    public void Renders_NotInstalled_ShowsSetupButton()
    {
        var cut = RenderSectionWithHeader(MakeTsServer(installed: false));
        var markup = cut.Markup;

        Assert.Contains("TeamspeakSetup", markup);
    }

    [Fact]
    public void Renders_Installed_ShowsTabs()
    {
        var cut = RenderSection();
        var markup = cut.Markup;

        Assert.Contains("Clients", markup);
        Assert.Contains("Channels", markup);
        Assert.Contains("Bans", markup);
        Assert.Contains("Logs", markup);
    }

    [Fact]
    public async Task Renders_ClientList_ShowsNicknames()
    {
        var cut = RenderSection();
        var load = typeof(ServerTeamspeakSection).GetMethod(
            "LoadClientsDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)load.Invoke(cut.Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Player1", cut.Markup);
            Assert.Contains("Player2", cut.Markup);
            Assert.DoesNotContain("serveradmin", cut.Markup);
        });
    }

    [Fact]
    public async Task Renders_ChannelList_ShowsChannelNames()
    {
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/servers/5/teamspeak?tab=channels");
        var cut = RenderSection();
        var load = typeof(ServerTeamspeakSection).GetMethod(
            "LoadChannelsDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)load.Invoke(cut.Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Default", cut.Markup);
            Assert.Contains("AFK Room", cut.Markup);
        });
    }

    [Fact]
    public void Renders_ServerInfo_ShowsNameAndClients()
    {
        var cut = RenderSection();
        var markup = cut.Markup;

        Assert.Contains("My TS Server", markup);
        Assert.Contains("5/32", markup);
    }

    [Fact]
    public void Renders_Uptime_FormatsCorrectly()
    {
        var cut = RenderSection();
        var markup = cut.Markup;

        Assert.Contains("2h 0m", markup);
    }

    [Fact]
    public void Renders_NotRunning_ShowsStoppedBadge()
    {
        var cut = RenderSection(MakeTsServer(installed: true, running: false));
        var markup = cut.Markup;

        Assert.Contains("Stopped", markup);
    }

    // --- Dialog models ---

    [Fact]
    public void KickDialog_RendersClientAndReasonField()
    {
        var cut = Render<TeamspeakOperationDialog>(parameters => parameters
            .Add(p => p.Mode, TeamspeakDialogMode.Kick)
            .Add(p => p.Model, new TeamspeakDialogModel { ClientId = 42, Nickname = "BadPlayer" }));

        Assert.Equal("BadPlayer", cut.Instance.Model.Nickname);
        Assert.Contains("input", cut.Markup);
    }

    [Fact]
    public void BanDialog_RendersClientAndDurationField()
    {
        var cut = Render<TeamspeakOperationDialog>(parameters => parameters
            .Add(p => p.Mode, TeamspeakDialogMode.Ban)
            .Add(p => p.Model, new TeamspeakDialogModel { ClientUniqueId = "uidSpam", Nickname = "Spammer", Duration = 3600 }));

        Assert.Equal("Spammer", cut.Instance.Model.Nickname);
        Assert.Contains("3600", cut.Markup);
    }

    [Fact]
    public void EditChannelDialog_RendersExistingValues()
    {
        var cut = Render<TeamspeakOperationDialog>(parameters => parameters
            .Add(p => p.Mode, TeamspeakDialogMode.EditChannel)
            .Add(p => p.Model, new TeamspeakDialogModel { ChannelId = 5, Name = "Old Name", MaxClients = 10 }));

        Assert.Contains("Old Name", cut.Markup);
        Assert.Contains("10", cut.Markup);
    }

    [Fact]
    public async Task HandleTaskCompleted_TriggersRender()
    {
        var cut = RenderSection();
        var renderCount = cut.RenderCount;

        await cut.InvokeAsync(async () => await cut.Instance.HandleTaskCompletedAsync(new TaskCompletedNotification
        {
            ServerId = 5,
            TaskName = "TeamSpeak - refresh"
        }));

        Assert.True(cut.RenderCount > renderCount);
    }

    [Fact]
    public async Task FailedOperation_ShowsErrorWithoutSuccessNotification()
    {
        _handler.SetResponse(HttpMethod.Post, "api/servers/5/teamspeak/action", HttpStatusCode.BadRequest);
        var cut = RenderSection();
        var method = typeof(ServerTeamspeakSection).GetMethod(
            "ExecuteActionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [TeamspeakAction.Restart])!);

        var messages = Services.Toasts();
        Assert.Contains(messages, message => message.Severity == OmniSeverity.Danger);
        Assert.DoesNotContain(messages, message => message.Severity == OmniSeverity.Success);
    }

    [Fact]
    public void ServerChange_ClearsOperationTargetsAndOldCollections()
    {
        var cut = RenderSection();
        typeof(ServerTeamspeakSection).GetField(
            "_banClientUniqueId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, "old-server-user");
        var next = MakeTsServer() with { Id = 6, Name = "other" };
        _handler.SetJsonResponse("api/servers/6/teamspeak",
            next.Teamspeak with { Clients = [], Channels = [], Bans = [] });
        _handler.SetPaginatedJsonResponse("api/servers/6/teamspeak/clients", Array.Empty<TeamspeakClientDto>());
        _handler.SetPaginatedJsonResponse("api/servers/6/teamspeak/channels", Array.Empty<TeamspeakChannelDto>());
        _handler.SetPaginatedJsonResponse("api/servers/6/teamspeak/bans", Array.Empty<TeamspeakBanDto>());

        cut.Render(parameters => parameters
            .Add(component => component.Server, next)
            .Add(component => component.ServerId, 6));

        var target = (string)typeof(ServerTeamspeakSection).GetField(
            "_banClientUniqueId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Empty(target);
    }

    [Fact]
    public async Task TaskCompletedForAnotherServer_DoesNotRefresh()
    {
        var cut = RenderSection();
        var before = _handler.Requests.Count;

        await cut.Instance.HandleTaskCompletedAsync(new TaskCompletedNotification
        {
            ServerId = 99,
            TaskName = "TeamSpeak - refresh"
        });

        Assert.Equal(before, _handler.Requests.Count);
    }

    [Fact]
    public void FormatUptime_DaysAndHours()
    {
        var method = typeof(ServerTeamspeakSection)
            .GetMethod("FormatUptime", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method.Invoke(null, [90000L])!;
        Assert.Contains("1d", result);
        Assert.Contains("h", result);
    }

    [Fact]
    public void FormatUptime_HoursOnly()
    {
        var method = typeof(ServerTeamspeakSection)
            .GetMethod("FormatUptime", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method.Invoke(null, [3661L])!;
        Assert.Contains("1h", result);
        Assert.DoesNotContain("d", result);
    }
}
