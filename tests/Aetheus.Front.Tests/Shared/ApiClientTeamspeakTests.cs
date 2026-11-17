// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests;

/// <summary>Covers all ApiClient.Teamspeak methods (59 uncovered lines).</summary>
public class ApiClientTeamspeakTests
{
    private readonly BunitTestHelper.TestHandler _handler = new();
    private readonly ApiClient _api;

    public ApiClientTeamspeakTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://test/") };
        _api = new ApiClient(http);
        _handler.SetJsonResponse("api/servers/1/teamspeak", new TeamspeakDataDto { IsInstalled = true, IsRunning = true, Version = "3.13.7", OnlineClients = 4 });
        _handler.SetJsonResponse("api/servers/1/teamspeak/action", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/setup", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/logs", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/kick", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/ban", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/move-client", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/poke", "{}");
        _handler.SetPaginatedJsonResponse("api/servers/1/teamspeak/clients",
            new[] { new TeamspeakClientDto { ClientId = 1, Nickname = "Alice" } });
        _handler.SetPaginatedJsonResponse("api/servers/1/teamspeak/bans",
            new[] { new TeamspeakBanDto { BanId = 2, Nickname = "Blocked" } });
        _handler.SetPaginatedJsonResponse("api/servers/1/teamspeak/channels",
            new[] { new TeamspeakChannelDto { Id = 3, Name = "Lobby" } });
        _handler.SetJsonResponse("api/servers/1/teamspeak/server", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/message", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/clientinfo", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/graceful-restart", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/snapshots", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/snapshots/deploy", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/server-groups", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/server-groups/add", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/server-groups/remove", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/tokens", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/server-info", "{}");
        _handler.SetJsonResponse("api/servers/1/teamspeak/complaints", "{}");
    }

    [Fact]
    public async Task GetTeamspeakStateAsync_ReturnsData()
    { var r = await _api.Teamspeak.GetTeamspeakStateAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.IsRunning); Assert.Equal("3.13.7", r.Version); Assert.Equal(4, r.OnlineClients); _handler.LastSent(HttpMethod.Get, "api/servers/1/teamspeak"); }

    [Fact]
    public async Task ExecuteTeamspeakActionAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.ExecuteTeamspeakActionAsync(1, new TeamspeakActionRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/action", new TeamspeakActionRequest()); }

    [Fact]
    public async Task SetupTeamspeakAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.SetupTeamspeakAsync(1, new TeamspeakSetupRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/setup", new TeamspeakSetupRequest()); }

    [Fact]
    public async Task GetTeamspeakLogsAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.GetTeamspeakLogsAsync(1, new TeamspeakLogRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/logs", new TeamspeakLogRequest()); }

    [Fact]
    public async Task KickTeamspeakClientAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.KickTeamspeakClientAsync(1, new TeamspeakKickRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/kick", new TeamspeakKickRequest()); }

    [Fact]
    public async Task BanTeamspeakClientAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.BanTeamspeakClientAsync(1, new TeamspeakBanRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/ban", new TeamspeakBanRequest()); }

    [Fact]
    public async Task MoveTeamspeakClientAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.MoveTeamspeakClientAsync(1, new TeamspeakMoveClientRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/move-client", new TeamspeakMoveClientRequest()); }

    [Fact]
    public async Task PokeTeamspeakClientAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.PokeTeamspeakClientAsync(1, new TeamspeakPokeClientRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/poke", new TeamspeakPokeClientRequest()); }

    [Fact]
    public async Task GetTeamspeakBansAsync_ReturnsPage()
    { var r = await _api.Teamspeak.GetTeamspeakBansAsync(1, 1, 25, ct: Xunit.TestContext.Current.CancellationToken); Assert.Equal(2, Assert.Single(r.Items).BanId); _handler.LastSent(HttpMethod.Get, "api/servers/1/teamspeak/bans?page=1&pageSize=25&sortDescending=false"); }

    [Fact]
    public async Task GetTeamspeakClientsAsync_ReturnsPage()
    { var r = await _api.Teamspeak.GetTeamspeakClientsAsync(1, 1, 25, ct: Xunit.TestContext.Current.CancellationToken); Assert.Equal("Alice", Assert.Single(r.Items).Nickname); _handler.LastSent(HttpMethod.Get, "api/servers/1/teamspeak/clients?page=1&pageSize=25&sortDescending=false"); }

    [Fact]
    public async Task GetTeamspeakChannelsAsync_ReturnsPage()
    { var r = await _api.Teamspeak.GetTeamspeakChannelsAsync(1, 1, 25, ct: Xunit.TestContext.Current.CancellationToken); Assert.Equal("Lobby", Assert.Single(r.Items).Name); _handler.LastSent(HttpMethod.Get, "api/servers/1/teamspeak/channels?page=1&pageSize=25&sortDescending=false"); }

    [Fact]
    public async Task UnbanTeamspeakClientAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.UnbanTeamspeakClientAsync(1, 42, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Delete, "api/servers/1/teamspeak/bans/42"); }

    [Fact]
    public async Task CreateTeamspeakChannelAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.CreateTeamspeakChannelAsync(1, new TeamspeakCreateChannelRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/channels", new TeamspeakCreateChannelRequest()); }

    [Fact]
    public async Task EditTeamspeakChannelAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.EditTeamspeakChannelAsync(1, 10, new TeamspeakEditChannelRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Put, "api/servers/1/teamspeak/channels/10", new TeamspeakEditChannelRequest()); }

    [Fact]
    public async Task DeleteTeamspeakChannelAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.DeleteTeamspeakChannelAsync(1, 10, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Delete, "api/servers/1/teamspeak/channels/10"); }

    [Fact]
    public async Task EditTeamspeakServerAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.EditTeamspeakServerAsync(1, new TeamspeakServerEditRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Put, "api/servers/1/teamspeak/server", new TeamspeakServerEditRequest()); }

    [Fact]
    public async Task SendTeamspeakGlobalMessageAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.SendTeamspeakGlobalMessageAsync(1, new TeamspeakGlobalMessageRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/message", new TeamspeakGlobalMessageRequest()); }

    [Fact]
    public async Task GetTeamspeakClientInfoAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.GetTeamspeakClientInfoAsync(1, new TeamspeakClientInfoRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/clientinfo", new TeamspeakClientInfoRequest()); }

    [Fact]
    public async Task TeamspeakGracefulRestartAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.TeamspeakGracefulRestartAsync(1, new TeamspeakGracefulRestartRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/graceful-restart", new TeamspeakGracefulRestartRequest()); }

    [Fact]
    public async Task CreateTeamspeakSnapshotAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.CreateTeamspeakSnapshotAsync(1, new TeamspeakSnapshotCreateRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/snapshots", new TeamspeakSnapshotCreateRequest()); }

    [Fact]
    public async Task DeployTeamspeakSnapshotAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.DeployTeamspeakSnapshotAsync(1, new TeamspeakSnapshotDeployRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/snapshots/deploy", new TeamspeakSnapshotDeployRequest()); }

    [Fact]
    public async Task ListTeamspeakServerGroupsAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.ListTeamspeakServerGroupsAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Get, "api/servers/1/teamspeak/server-groups"); }

    [Fact]
    public async Task AddTeamspeakServerGroupClientAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.AddTeamspeakServerGroupClientAsync(1, new TeamspeakServerGroupAddRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/server-groups/add", new TeamspeakServerGroupAddRequest()); }

    [Fact]
    public async Task RemoveTeamspeakServerGroupClientAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.RemoveTeamspeakServerGroupClientAsync(1, new TeamspeakServerGroupRemoveRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/server-groups/remove", new TeamspeakServerGroupRemoveRequest()); }

    [Fact]
    public async Task ListTeamspeakTokensAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.ListTeamspeakTokensAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Get, "api/servers/1/teamspeak/tokens"); }

    [Fact]
    public async Task CreateTeamspeakTokenAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.CreateTeamspeakTokenAsync(1, new TeamspeakTokenCreateRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Post, "api/servers/1/teamspeak/tokens", new TeamspeakTokenCreateRequest()); }

    [Fact]
    public async Task DeleteTeamspeakTokenAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.DeleteTeamspeakTokenAsync(1, new TeamspeakTokenDeleteRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Delete, "api/servers/1/teamspeak/tokens", new TeamspeakTokenDeleteRequest()); }

    [Fact]
    public async Task GetTeamspeakServerInfoAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.GetTeamspeakServerInfoAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Get, "api/servers/1/teamspeak/server-info"); }

    [Fact]
    public async Task ListTeamspeakComplaintsAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.ListTeamspeakComplaintsAsync(1, Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Get, "api/servers/1/teamspeak/complaints"); }

    [Fact]
    public async Task DeleteTeamspeakComplaintAsync_ReturnsStatus()
    { var r = await _api.Teamspeak.DeleteTeamspeakComplaintAsync(1, new TeamspeakComplaintDeleteRequest(), Xunit.TestContext.Current.CancellationToken); Assert.True(r.Success); _handler.LastSent(HttpMethod.Delete, "api/servers/1/teamspeak/complaints", new TeamspeakComplaintDeleteRequest()); }
}
