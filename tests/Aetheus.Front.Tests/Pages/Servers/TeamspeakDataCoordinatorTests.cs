// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;

namespace Aetheus.Front.Tests.Pages.Servers;

public sealed class TeamspeakDataCoordinatorTests
{
    [Fact]
    public async Task RefreshState_FetchesAndPublishesNewServerState()
    {
        // The server's heartbeat push refreshes the state here; the grids on screen then refresh
        // themselves quietly through their own LoadData (see ServerTeamspeakSectionRenderTests).
        var handler = new BunitTestHelper.TestHandler();
        handler.SetJsonResponse("api/servers/5/teamspeak",
            new TeamspeakDataDto { IsInstalled = true, ServerName = "Before" });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        await using var coordinator = new TeamspeakDataCoordinator(new ApiClient(http));
        var published = 0;

        await coordinator.ResetAsync(
            5,
            new TeamspeakDataDto { IsInstalled = true, ServerName = "Initial" },
            () => { published++; return Task.CompletedTask; });
        handler.SetJsonResponse("api/servers/5/teamspeak",
            new TeamspeakDataDto { IsInstalled = true, ServerName = "After" });
        var before = published;

        await coordinator.RefreshStateAsync();

        Assert.Equal("After", coordinator.State.ServerName);
        Assert.True(published > before);
    }

    [Fact]
    public async Task LoadBans_SendsTheGridHeaderFiltersToTheApi()
    {
        // A header filter of the bans grid reaches the endpoint as a column filter.
        var handler = new BunitTestHelper.TestHandler();
        handler.SetJsonResponse("api/servers/5/teamspeak", new TeamspeakDataDto { IsInstalled = true });
        handler.SetPaginatedJsonResponse("api/servers/5/teamspeak/bans",
            new[] { new TeamspeakBanDto { BanId = 2, Nickname = "new" } });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        await using var coordinator = new TeamspeakDataCoordinator(new ApiClient(http));
        await coordinator.ResetAsync(5, new TeamspeakDataDto(), () => Task.CompletedTask);

        await coordinator.LoadBansAsync(1, 25, null, "Created", true,
            [new Aetheus.Shared.Components.Shared.GridFilter { Field = "Nickname", Operator = Aetheus.Shared.Components.Shared.GridFilterOperator.Contains, Value = "ne" }]);

        Assert.Equal(2, Assert.Single(coordinator.Bans).BanId);
        Assert.Contains(handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("teamspeak/bans", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Nickname", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=ne", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetAsync_ClearsPreviousServerCollections()
    {
        var handler = new BunitTestHelper.TestHandler();
        handler.SetJsonResponse("api/servers/5/teamspeak", new TeamspeakDataDto { IsInstalled = true });
        handler.SetPaginatedJsonResponse("api/servers/5/teamspeak/clients",
            new[] { new TeamspeakClientDto { ClientId = 1, Nickname = "Old" } });
        handler.SetJsonResponse("api/servers/6/teamspeak", new TeamspeakDataDto { IsInstalled = true });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        await using var coordinator = new TeamspeakDataCoordinator(new ApiClient(http));

        await coordinator.ResetAsync(5, new TeamspeakDataDto(), () => Task.CompletedTask);
        await coordinator.LoadClientsAsync(1, 25, null, "Nickname", false);
        Assert.Single(coordinator.Clients);

        await coordinator.ResetAsync(6, new TeamspeakDataDto(), () => Task.CompletedTask);

        Assert.Empty(coordinator.Clients);
        Assert.Equal(0, coordinator.ClientsCount);
    }
}
