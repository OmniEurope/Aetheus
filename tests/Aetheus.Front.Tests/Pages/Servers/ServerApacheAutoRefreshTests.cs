// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-17: while the Apache page is open, its state and data are read again every 15 s with no manual reload (an
/// Apache restarted on the host shows), the refreshes never overlap, nothing is read while a task of the page waits for
/// the server's next state, and leaving the page stops the timer.
/// </summary>
public sealed class ServerApacheAutoRefreshTests : BunitContext
{
    private const int ServerId = 5;

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly FakeTimeProvider _clock = new();

    public ServerApacheAutoRefreshTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddScoped(sp => new ServerDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance));
        _handler.SetJsonResponse(HttpMethod.Get, "api/monitoring/servers/", new List<ServerMetricDto>());
    }

    private ServerDetailLoader Loader => Services.GetRequiredService<ServerDetailLoader>();

    private int ServerReads() => _handler.Requests.Count(r => r.Url.EndsWith("api/servers/5", StringComparison.Ordinal));

    private void ServeApache(bool running) =>
        _handler.SetJsonResponse("api/servers/5", new ServerDetailDto
        {
            Id = ServerId,
            Name = "srv",
            Hostname = "10.0.0.5",
            Type = ServerType.Normal,
            Status = ServerStatus.Online,
            Tags = [],
            Services = [],
            Apache = new ApacheDataDto { IsRunning = running }
        });

    private async Task<IRenderedComponent<ServerApacheSection>> RenderPageAsync()
    {
        ServeApache(running: false);
        await Loader.EnsureLoadedAsync(ServerId, Xunit.TestContext.Current.CancellationToken);
        return Render<ServerApacheSection>(p => p
            .AddCascadingValue("ServerLoader", Loader)
            .Add(x => x.ServerId, ServerId)
            .Add(x => x.Server, Loader.Server!));
    }

    [Fact]
    public async Task R17_TheOpenPage_ReadsTheServerAgainEvery15Seconds_AndShowsAnApacheRestartedOnTheHost()
    {
        await RenderPageAsync();
        Assert.False(Loader.Server!.Apache.IsRunning);
        var before = ServerReads();

        ServeApache(running: true);
        _clock.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(before, ServerReads());

        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => ServerReads() == before + 1);
        await WaitForAsync(() => Loader.Server!.Apache.IsRunning);

        _clock.Advance(TimeSpan.FromSeconds(15));
        await WaitForAsync(() => ServerReads() == before + 2);
    }

    [Fact]
    public async Task R17_LeavingThePage_StopsTheTimer()
    {
        await RenderPageAsync();
        var before = ServerReads();

        await DisposeComponentsAsync();
        _clock.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(before, ServerReads());
    }

    [Fact]
    public async Task R17_AnActionOfThePageInProgress_PostponesTheRefresh_ItsFollowerWaitsForTheServersNextState()
    {
        var cut = await RenderPageAsync();
        _handler.SetJsonResponse(HttpMethod.Post, "apache/action", new ServiceTaskResponse { TaskId = 50301 });
        await cut.InvokeAsync(() => cut.Instance.ExecuteActionAsync(ApacheAction.DisableSite, "alpha.conf"));
        await cut.InvokeAsync(() => Services.GetRequiredService<TaskTrackerService>().Upsert(
            new ServerTaskDto { Id = 50301, ServerId = ServerId, Name = "Apache DisableSite - alpha.conf", Status = TaskExecutionStatus.Running }));
        var before = ServerReads();

        _clock.Advance(TimeSpan.FromSeconds(45));
        await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(before, ServerReads());
    }

    [Fact]
    public async Task R17_ASlowRefresh_IsNeverOverlappedByTheNextTick()
    {
        var started = 0;
        var release = new TaskCompletionSource();
        using var refresh = new ServerPageAutoRefresh(_clock, () => false, async () =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
        });

        _clock.Advance(TimeSpan.FromSeconds(15));
        _clock.Advance(TimeSpan.FromSeconds(15));
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(1, Volatile.Read(ref started));

        release.SetResult();
        // The first refresh ends on its own time: a tick that finds it still running is skipped, so the clock moves
        // by one interval again until the next refresh starts, instead of guessing how long the first one takes.
        await WaitForAsync(() =>
        {
            _clock.Advance(TimeSpan.FromSeconds(15));
            return Volatile.Read(ref started) >= 2;
        });
        Assert.Equal(2, Volatile.Read(ref started));
    }

    [Fact]
    public async Task R17_AFailedRefresh_IsRetriedAtTheNextTick_WithoutBreakingThePage()
    {
        var calls = 0;
        using var refresh = new ServerPageAutoRefresh(_clock, () => false, () =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("backend unreachable");
        });

        _clock.Advance(TimeSpan.FromSeconds(15));
        await WaitForAsync(() => Volatile.Read(ref calls) == 1);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await WaitForAsync(() => Volatile.Read(ref calls) == 2);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition was not met within 30 s");
            await Task.Delay(20, Xunit.TestContext.Current.CancellationToken);
        }
    }
}
