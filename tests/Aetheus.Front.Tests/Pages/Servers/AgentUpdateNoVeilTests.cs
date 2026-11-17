// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-37 (replaces the R-19 veil of the agent update): updating the agent never veils the page. The progress card
/// stays visible from the queueing to the end, the page veil stays down (so the page buttons stay usable), and leaving the
/// page leaves nothing held.
/// </summary>
public sealed class AgentUpdateNoVeilTests : BunitContext
{
    private readonly InMemoryHubConnectionFactory _hubs;
    private readonly HubConnection _hub;

    public AgentUpdateNoVeilTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddScoped(sp => new ServerDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance));
        _hubs = new InMemoryHubConnectionFactory(Services.GetRequiredService<AuthStateProvider>());
        _hub = _hubs.Create("servers");
    }

    private ServerDetailLoader Loader => Services.GetRequiredService<ServerDetailLoader>();

    private InMemoryHubServer Server => _hubs.Server("servers");

    private async Task<IRenderedComponent<AgentUpdateProgressCard>> RenderCardAsync()
    {
        await _hub.StartAsync(Xunit.TestContext.Current.CancellationToken);
        return Render<AgentUpdateProgressCard>(parameters => parameters
            .AddCascadingValue("ServerLoader", Loader)
            .Add(card => card.ServerId, 4)
            .Add(card => card.Hub, _hub));
    }

    private bool PageVeiled => Services.GetRequiredService<BusyVeil>().IsBusy || Loader.TaskPending;

    [Fact]
    public async Task AnUpdateInProgress_NeverVeilsThePage_AndTheCardStaysVisible_ThroughEveryPhase()
    {
        var cut = await RenderCardAsync();
        Assert.False(PageVeiled);

        await Server.PushAsync("AgentUpdateQueued", 4);
        await cut.WhenRenderedAsync(() => Assert.Contains("AgentUpdatePhase_Queued", cut.Markup, StringComparison.Ordinal));
        Assert.True(cut.Instance.InProgress);
        Assert.False(PageVeiled, "a queued update does not veil the page");

        await Server.PushAsync("AgentUpdateProgress", new AgentUpdateProgressDto { ServerId = 4, Phase = AgentUpdatePhase.Downloading, Percent = 20 });
        await cut.WhenRenderedAsync(() => Assert.Contains("AgentUpdatePhase_Downloading", cut.Markup, StringComparison.Ordinal));
        Assert.False(PageVeiled, "the progress card is visible and the page is not veiled");

        await Server.PushAsync("AgentUpdateFailed", 4, 9);
        await cut.WhenRenderedAsync(() => Assert.Contains("AgentUpdatePhase_Failed", cut.Markup, StringComparison.Ordinal));
        Assert.False(PageVeiled);
    }

    [Fact]
    public async Task AConfirmedUpdate_NeverVeiledThePage()
    {
        var cut = await RenderCardAsync();
        await Server.PushAsync("AgentUpdateQueued", 4);
        await cut.WhenRenderedAsync(() => Assert.True(cut.Instance.InProgress));
        Assert.False(PageVeiled);

        await Server.PushAsync("AgentUpdateConfirmed", 4, 9);

        await cut.WhenRenderedAsync(() => Assert.False(cut.Instance.InProgress));
        Assert.False(PageVeiled);
    }

    [Fact]
    public async Task TheUpdateOfAnotherServer_ShowsNothingAndVeilsNothing()
    {
        var cut = await RenderCardAsync();

        await Server.PushAsync("AgentUpdateQueued", 99);

        Assert.False(PageVeiled);
        Assert.DoesNotContain("AgentUpdatePhase_Queued", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingThePage_DuringAnUpdate_LeavesNothingHeld()
    {
        var cut = await RenderCardAsync();
        await Server.PushAsync("AgentUpdateQueued", 4);
        await cut.WhenRenderedAsync(() => Assert.True(cut.Instance.InProgress));

        await DisposeComponentsAsync();

        Assert.False(PageVeiled);
    }
}
