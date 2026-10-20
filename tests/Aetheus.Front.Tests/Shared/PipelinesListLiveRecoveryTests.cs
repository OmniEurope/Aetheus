// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The pipelines list keeps trying its two realtime hubs every five seconds when they could not start:
/// a pass that still fails is retried on the next one, a pass that succeeds joins the updates group, the
/// running runs' groups and the pipeline entity updates, then reloads the list once; a connected hub is
/// left alone, and leaving the page stops the retries. Real hub connections on in-memory hubs, simulated
/// clock: a pass is run by advancing the clock once its delay is armed, and is over when the next delay is.
/// </summary>
public sealed class PipelinesListLiveRecoveryTests : BunitContext
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ObservedFakeClock _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    public PipelinesListLiveRecoveryTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
        Services.AddSingleton<TimeProvider>(_clock);
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        _handler.SetJsonResponse("api/pipelines/dependencies", new PipelineDependencyGroupsDto
        {
            Leaves = [new PipelineDependencyDto { Id = 7, Name = "Build" }]
        });
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>
        {
            new() { Id = 41, PipelineId = 7, Status = PipelineStatus.Running },
            new() { Id = 42, PipelineId = 7, Status = PipelineStatus.Success }
        });
    }

    private IRenderedComponent<ContainerFragment>? _host;

    private InMemoryHubServer Pipelines => this.Hub("pipelines");
    private InMemoryHubServer Entities => this.Hub("entities");

    private int DependencyReads() =>
        _handler.RequestsSnapshot().Count(request => request.Method == "GET"
            && request.Url.Contains("api/pipelines/dependencies", StringComparison.Ordinal));

    private async Task RenderWithUnreachableHubsAsync()
    {
        Pipelines.RefuseConnections = true;
        Entities.RefuseConnections = true;
        _host = Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), PipelinesList.HeaderActionsSection);
            builder.CloseComponent();
            builder.OpenComponent<PipelinesList>(2);
            builder.CloseComponent();
        });
        await _clock.WhenArmedAsync(RetryInterval, 1);
        Assert.Equal((1, 1), (Pipelines.ConnectAttempts, Entities.ConnectAttempts));
    }

    /// <summary>Runs one recovery pass: the clock reaches the armed delay, and the pass is over once the
    /// loop arms the next one.</summary>
    private async Task PassAsync()
    {
        var armed = _clock.Armed(RetryInterval);
        Assert.Equal(1, _clock.Pending(RetryInterval));
        _clock.Advance(RetryInterval);
        await _clock.WhenArmedAsync(RetryInterval, armed + 1);
    }

    [Fact]
    public async Task AFailedRetry_IsTriedAgain_AndTheFirstHubFailureSkipsTheSecondForThatPass()
    {
        await RenderWithUnreachableHubsAsync();

        await PassAsync();
        await PassAsync();

        // The pass stops at the pipelines hub that still refuses: the entities hub is not retried.
        Assert.Equal(3, Pipelines.ConnectAttempts);
        Assert.Equal(1, Entities.ConnectAttempts);
        Assert.Empty(Pipelines.Invocations);
        Assert.Empty(Entities.Invocations);
    }

    [Fact]
    public async Task ARetryThatConnects_JoinsEveryGroup_AndReloadsTheListOnce()
    {
        await RenderWithUnreachableHubsAsync();
        var readsBefore = DependencyReads();

        Pipelines.RefuseConnections = false;
        Entities.RefuseConnections = false;
        await PassAsync();

        // Only the running run's group is joined, and the list is read again once the hubs are back.
        Assert.Equal(1, Pipelines.Count("JoinPipelineUpdatesGroup"));
        Assert.Equal(1, Entities.Count("JoinEntityUpdates"));
        Assert.True(DependencyReads() > readsBefore);
        Assert.Equal([41], Pipelines.IntArguments("JoinPipelineRunGroup"));
        Assert.Equal((int)ResourceType.Pipeline, ResourceTypeArgument(Entities));

        // Connected hubs are not started again by later passes.
        for (var pass = 0; pass < 3; pass++) await PassAsync();
        Assert.Equal((1, 1), (Pipelines.Connections, Entities.Connections));
        Assert.Equal(1, Pipelines.Count("JoinPipelineUpdatesGroup"));
    }

    [Fact]
    public async Task OnlyTheEntityHubDown_IsRecoveredAlone()
    {
        Entities.RefuseConnections = true;
        _host = Render(builder =>
        {
            builder.OpenComponent<PipelinesList>(0);
            builder.CloseComponent();
        });
        await _clock.WhenArmedAsync(RetryInterval, 1);
        Assert.Equal(1, Pipelines.Count("JoinPipelineUpdatesGroup"));
        Assert.Equal(1, Entities.ConnectAttempts);

        Entities.RefuseConnections = false;
        await PassAsync();

        Assert.Equal(1, Entities.Count("JoinEntityUpdates"));
        Assert.Equal(1, Pipelines.Connections);
        Assert.Equal(1, Pipelines.Count("JoinPipelineUpdatesGroup"));
    }

    [Fact]
    public async Task LeavingThePage_StopsTheRetries()
    {
        await RenderWithUnreachableHubsAsync();
        await PassAsync();
        Assert.Equal(2, Pipelines.ConnectAttempts);

        var list = _host!.FindComponent<PipelinesList>();
        await list.InvokeAsync(async () => await list.Instance.DisposeAsync());
        var attempts = Pipelines.ConnectAttempts;
        var armed = _clock.Armed(RetryInterval);

        // The pending delay was cancelled with the page: nothing is left that the clock could fire.
        Assert.Equal(0, _clock.Pending(RetryInterval));
        for (var pass = 0; pass < 3; pass++) _clock.Advance(RetryInterval);

        Assert.Equal(armed, _clock.Armed(RetryInterval));
        Assert.Equal(attempts, Pipelines.ConnectAttempts);
    }

    private static int ResourceTypeArgument(InMemoryHubServer hub)
    {
        var argument = hub.Invocations.Single(invocation => invocation.Target == "JoinEntityUpdates").Arguments[0];
        return argument.ValueKind == System.Text.Json.JsonValueKind.Number
            ? argument.GetInt32()
            : (int)Enum.Parse<ResourceType>(argument.GetString()!);
    }
}
