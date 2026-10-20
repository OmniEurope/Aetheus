// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// The agent update card driven by the hub events of a real connection to an in-memory hub: a queued
/// update shows, progress advances and never goes back (a failure always shows), the agent going offline
/// counts only after the updater was launched, a failure shows the failed state, and events of another
/// server change nothing.
/// </summary>
public sealed class AgentUpdateProgressCardLiveTests : BunitContext
{
    private readonly InMemoryHubConnectionFactory _hubs;
    private readonly HubConnection _hub;

    public AgentUpdateProgressCardLiveTests()
    {
        BunitTestHelper.RegisterServices(this);
        _hubs = new InMemoryHubConnectionFactory(Services.GetRequiredService<AuthStateProvider>());
        _hub = _hubs.Create("servers");
    }

    private InMemoryHubServer Server => _hubs.Server("servers");

    private async Task<IRenderedComponent<AgentUpdateProgressCard>> RenderCardAsync()
    {
        await _hub.StartAsync(Xunit.TestContext.Current.CancellationToken);
        return Render<AgentUpdateProgressCard>(parameters => parameters
            .Add(card => card.ServerId, 4)
            .Add(card => card.Hub, _hub)
            .Add(card => card.CurrentAgentVersion, "1.0.0"));
    }

    private static Task Progress(InMemoryHubServer server, int serverId, AgentUpdatePhase phase, int percent, string? message = null) =>
        server.PushAsync("AgentUpdateProgress", new AgentUpdateProgressDto { ServerId = serverId, Phase = phase, Percent = percent, Message = message });

    [Fact]
    public async Task AQueuedUpdate_Shows_AndProgressNeverGoesBack()
    {
        var cut = await RenderCardAsync();

        await Server.PushAsync("AgentUpdateQueued", 4);
        await cut.WhenRenderedAsync(() => Assert.Contains("AgentUpdatePhase_Queued", cut.Markup, StringComparison.Ordinal));

        await Progress(Server, 4, AgentUpdatePhase.Extracting, 60, "extracting archive");
        await cut.WhenRenderedAsync(() => Assert.Contains("AgentUpdatePhase_Extracting", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("extracting archive", cut.Markup, StringComparison.Ordinal);

        await Progress(Server, 4, AgentUpdatePhase.Downloading, 20, "late download beat");
        Assert.Contains("AgentUpdatePhase_Extracting", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("late download beat", cut.Markup, StringComparison.Ordinal);

        await Progress(Server, 4, AgentUpdatePhase.Failed, 0, "disk full");
        await cut.WhenRenderedAsync(() => Assert.Contains("AgentUpdatePhase_Failed", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAgentGoingOffline_CountsOnlyAfterTheUpdaterWasLaunched()
    {
        var cut = await RenderCardAsync();
        await Progress(Server, 4, AgentUpdatePhase.Downloading, 20);
        await cut.WhenRenderedAsync(() => Assert.Contains("AgentUpdatePhase_Downloading", cut.Markup, StringComparison.Ordinal));

        await Server.PushAsync("ServerOffline", 4);
        Assert.DoesNotContain("AgentUpdatePhase_AgentOffline", cut.Markup, StringComparison.Ordinal);

        await Progress(Server, 4, AgentUpdatePhase.LaunchingUpdater, 80);
        await Server.PushAsync("ServerOffline", 4);
        await cut.WhenRenderedAsync(() =>
        {
            Assert.Contains("AgentUpdatePhase_AgentOffline", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("AgentUpdateRestartingHint", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AFailedUpdate_ShowsTheFailure()
    {
        var cut = await RenderCardAsync();
        await Server.PushAsync("AgentUpdateQueued", 4);
        await cut.WhenRenderedAsync(() => Assert.Contains("AgentUpdatePhase_Queued", cut.Markup, StringComparison.Ordinal));

        await Server.PushAsync("AgentUpdateFailed", 4, 31);

        await cut.WhenRenderedAsync(() =>
        {
            Assert.Contains("AgentUpdatePhase_Failed", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("AgentUpdateFailed", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EventsOfAnotherServer_ChangeNothing()
    {
        var cut = await RenderCardAsync();

        await Server.PushAsync("AgentUpdateQueued", 5);
        await Progress(Server, 5, AgentUpdatePhase.Extracting, 60);
        await Server.PushAsync("ServerOffline", 5);
        await Server.PushAsync("AgentUpdateFailed", 5, 31);
        await Server.PushAsync("AgentUpdateConfirmed", 5, 31);

        Assert.DoesNotContain("AgentUpdatePhase_", cut.Markup, StringComparison.Ordinal);
    }
}
