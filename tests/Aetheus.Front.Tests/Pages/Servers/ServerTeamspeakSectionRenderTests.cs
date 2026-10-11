// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerTeamspeakSectionRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags StaticPriv = BindingFlags.NonPublic | BindingFlags.Static;

    public ServerTeamspeakSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeTsServer(
        bool installed = true,
        bool running = true,
        int serverId = 5) => new()
        {
            Id = serverId,
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
                OnlineClients = 3,
                ChannelCount = 2,
                UptimeSeconds = 7200,
                Channels =
            [
                new TeamspeakChannelDto
                {
                    Id = 1, Name = "General", ParentId = 0,
                    TotalClients = 3, MaxClients = -1,
                    IsDefault = true, IsPermanent = true
                },
                new TeamspeakChannelDto
                {
                    Id = 2, Name = "AFK", ParentId = 0,
                    TotalClients = 0, MaxClients = 10,
                    HasPassword = true, IsPermanent = true
                }
            ],
                Clients =
            [
                new TeamspeakClientDto
                {
                    ClientId = 1, Nickname = "Player1", ChannelId = 1,
                    ChannelName = "General",
                    Platform = "Windows", UniqueId = "uid1"
                },
                new TeamspeakClientDto
                {
                    ClientId = 2, Nickname = "Player2", ChannelId = 1,
                    ChannelName = "General",
                    Platform = "Linux", UniqueId = "uid2"
                }
            ]
            }
        };

    private void StubTeamspeakApi(ServerDetailDto server)
    {
        var serverId = server.Id;
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak",
            server.Teamspeak with { Clients = [], Channels = [], Bans = [] });
        _handler.SetPaginatedJsonResponse($"api/servers/{serverId}/teamspeak/clients", server.Teamspeak.Clients);
        _handler.SetPaginatedJsonResponse($"api/servers/{serverId}/teamspeak/channels", server.Teamspeak.Channels);
        _handler.SetPaginatedJsonResponse($"api/servers/{serverId}/teamspeak/bans", server.Teamspeak.Bans);
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/action", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/setup", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/kick", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/move", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/poke", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/ban", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/unban", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/logs", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/message", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/info", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/snapshot", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/groups", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/tokens", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/complaints", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/graceful-restart", new { });
        _handler.SetJsonResponse($"api/servers/{serverId}/teamspeak/client-info", new { });
    }

    private IRenderedComponent<ServerTeamspeakSection> RenderSection(
        ServerDetailDto? server = null,
        int serverId = 5)
    {
        var s = server ?? MakeTsServer(serverId: serverId);
        StubTeamspeakApi(s);
        return Render<ServerTeamspeakSection>(p => p
            .Add(x => x.Server, s)
            .Add(x => x.ServerId, serverId));
    }

    // ── Render ────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_InstalledServer_ShowsServerName()
    {
        var cut = RenderSection();
        Assert.Contains("My TS Server", cut.Markup);
    }

    [Fact]
    public void Renders_InstalledServer_ShowsVersion()
    {
        var cut = RenderSection();
        Assert.Contains("3.13.7", cut.Markup);
    }

    [Fact]
    public async Task Renders_InstalledServer_ShowsChannelNames()
    {
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/servers/5/teamspeak?tab=channels");
        var cut = RenderSection();
        var load = typeof(ServerTeamspeakSection).GetMethod(
            "LoadChannelsDataAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)load.Invoke(
            cut.Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);
        cut.WaitForAssertion(() => Assert.Contains("General", cut.Markup));
    }

    [Fact]
    public void Renders_NotInstalled_ShowsSetupAndHidesClientTabs()
    {
        var server = MakeTsServer(installed: false, running: false);
        var cut = RenderSection(server);

        // Not-installed branch: the "NotInstalled" badge + a Setup button, and the
        // installed-only Clients/Channels tabs and Running badge are absent.
        Assert.Contains("NotInstalled", cut.Markup);
        Assert.Contains("TeamspeakSetup", cut.Markup);
        Assert.DoesNotContain("Running", cut.Markup);
        Assert.DoesNotContain("Nickname", cut.Markup);
    }

    [Fact]
    public void Renders_InstalledButNotRunning_ProducesMarkup()
    {
        var server = MakeTsServer(installed: true, running: false);
        var cut = RenderSection(server);

        // Installed-but-stopped: the installed details (heading + version) still render.
        Assert.Contains("Teamspeak", cut.Markup);
        Assert.Contains("3.13.7", cut.Markup);
    }

    // ── Dialog rendering and validation ──────────────────────────────────────

    [Theory]
    [InlineData(TeamspeakDialogMode.Kick, "BadPlayer", "KickReason")]
    [InlineData(TeamspeakDialogMode.Move, "Mover", "TargetChannel")]
    [InlineData(TeamspeakDialogMode.Poke, "PokeMe", "PokeMessage")]
    [InlineData(TeamspeakDialogMode.Ban, "Cheater", "BanReason")]
    public void ClientDialog_RendersModeSpecificFields(TeamspeakDialogMode mode, string nickname, string expectedField)
    {
        var cut = Render<TeamspeakOperationDialog>(parameters => parameters
            .Add(p => p.Mode, mode)
            .Add(p => p.Model, new TeamspeakDialogModel { Nickname = nickname, TargetChannelId = 2 })
            .Add(p => p.Channels, new List<TeamspeakChannelDto> { new() { Id = 2, Name = "Gaming" } }));

        Assert.Equal(mode, cut.Instance.Mode);
        Assert.Equal(nickname, cut.Instance.Model.Nickname);
        Assert.Contains(expectedField, cut.Markup);
    }

    [Fact]
    public void EditChannelDialog_RendersChannelName()
    {
        var cut = Render<TeamspeakOperationDialog>(parameters => parameters
            .Add(p => p.Mode, TeamspeakDialogMode.EditChannel)
            .Add(p => p.Model, new TeamspeakDialogModel { ChannelId = 3, Name = "Gaming", MaxClients = 10 }));

        Assert.Contains("Gaming", cut.Markup);
    }

    [Fact]
    public void PokeDialog_RequiresNonWhitespaceMessage()
    {
        var model = new TeamspeakDialogModel { Mode = TeamspeakDialogMode.Poke, Message = "   ", L = new BunitTestHelper.StubLocalizer() };
        var results = model.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(model)).ToList();

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(TeamspeakDialogModel.Message)));
    }

    // ── ExecuteActionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteActionAsync_Start_CompletesAndClearsRunning()
    {
        var cut = RenderSection();
        var method = typeof(ServerTeamspeakSection).GetMethod("ExecuteActionAsync", Priv)!;

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [TeamspeakAction.Start])!);

        var running = (bool)typeof(ServerTeamspeakSection)
            .GetField("_actionRunning", Priv)!
            .GetValue(cut.Instance)!;
        Assert.False(running);
    }

    [Fact]
    public async Task ExecuteActionAsync_Stop_CompletesAndClearsRunning()
    {
        var cut = RenderSection();
        var method = typeof(ServerTeamspeakSection).GetMethod("ExecuteActionAsync", Priv)!;

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [TeamspeakAction.Stop])!);

        Assert.False((bool)typeof(ServerTeamspeakSection)
            .GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ts3server://10.0.0.5?port=9987", "10.0.0.5", 9987)]
    [InlineData("ts3server://myhost.example?port=9988", "myhost.example", 9988)]
    public void BuildConnectionLink_ReturnsExpectedUri(string expected, string host, int port)
    {
        var method = typeof(ServerTeamspeakSection).GetMethod("BuildConnectionLink", StaticPriv)!;
        Assert.Equal(expected, method.Invoke(null, [host, port]));
    }

    [Theory]
    [InlineData(3600, "1h 0m")]
    [InlineData(90, "0h 1m")]
    public void FormatUptime_FormatsCorrectly(long seconds, string expected)
    {
        var method = typeof(ServerTeamspeakSection).GetMethod("FormatUptime", StaticPriv)!;
        Assert.Equal(expected, method.Invoke(null, [seconds]));
    }

    [Fact]
    public void FormatUptime_MoreThanOneDay_IncludesDays()
    {
        var method = typeof(ServerTeamspeakSection).GetMethod("FormatUptime", StaticPriv)!;
        var result = (string)method.Invoke(null, [90_000L])!; // 1d 1h
        Assert.Contains("d", result);
    }

    // ── HandleTaskCompletedAsync ──────────────────────────────────────────────

    [Fact]
    public async Task HandleTaskCompletedAsync_RefreshesState()
    {
        var cut = RenderSection();
        var ex = await Record.ExceptionAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { ServerId = 5, TaskName = "TeamSpeak - refresh" }));
        Assert.Null(ex);
    }

    [Fact]
    public async Task R181_NoRefreshBansButton_AHeartbeatReloadsTheStateAndTheGridsOnScreen()
    {
        var cut = RenderSection();
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("RefreshBans", StringComparison.Ordinal));
        _handler.SetJsonResponse("api/servers/5/teamspeak",
            new TeamspeakDataDto { IsInstalled = true, IsRunning = true, ServerName = "Pushed name" });
        var stateReads = _handler.Requests.Count(r => r.Url.EndsWith("api/servers/5/teamspeak", StringComparison.Ordinal));

        await cut.Instance.LiveFeed!.OnHeartbeatAsync(5);

        Assert.Equal(stateReads + 1,
            _handler.Requests.Count(r => r.Url.EndsWith("api/servers/5/teamspeak", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Contains("Pushed name", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task R210_R226_AClientsHeaderFilterReachesTheApi_AndAHeartbeatRefreshKeepsIt()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<TeamspeakClientDto>>().Instance;

        await cut.InvokeAsync(() => grid.Grid!.SetFiltersAsync(new Dictionary<string, string?> { ["Nickname"] = "Play" }));

        static bool Filtered((string Method, string Url) request) =>
            Uri.UnescapeDataString(request.Url).Contains("teamspeak/clients", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Nickname", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=Play", StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, Filtered));
        var filteredReads = _handler.Requests.Count(Filtered);

        await cut.InvokeAsync(() => cut.Instance.LiveFeed!.OnHeartbeatAsync(5));

        // The heartbeat refreshes the clients grid through its own LoadData: the filter is still sent.
        cut.WaitForAssertion(() => Assert.True(_handler.Requests.Count(Filtered) > filteredReads));
    }

    [Fact]
    public async Task R181_AnotherServersHeartbeat_ReloadsNothing()
    {
        var cut = RenderSection();
        var requests = _handler.Requests.Count;

        await cut.Instance.LiveFeed!.OnHeartbeatAsync(6);

        Assert.Equal(requests, _handler.Requests.Count);
    }
}
