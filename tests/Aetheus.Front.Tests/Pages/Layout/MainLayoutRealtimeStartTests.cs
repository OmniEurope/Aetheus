// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Layout;

/// <summary>
/// PLAN-010 lot 4, sub-lot C (2026-10-17): a directly opened address stayed 12 to 30 s on the splash of a
/// loaded machine, with "The realtime server did not answer within 10 s" in the console. The layout
/// awaited the task tracker's hub (start, JoinAllServers, active tasks, then a liveness probe on failure)
/// before declaring the application ready, so the page waited for the realtime server. The page must not:
/// the hub is a live affordance that reports its own state (header icon, connection-lost overlay).
/// </summary>
public sealed class MainLayoutRealtimeStartTests : BunitContext
{
    public MainLayoutRealtimeStartTests()
    {
        BunitTestHelper.RegisterServices(this);
        // A realtime server that never answers, on an attempt deadline that never elapses: without the
        // fix the splash waits for an answer that does not come.
        Services.AddSingleton<HubConnectionFactory>(services => new BunitTestHelper.BlockingHubConnectionFactory(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<AuthStateProvider>()));
        Services.AddSingleton(services => new TaskTrackerService(
            services.GetRequiredService<ApiClient>(),
            services.GetRequiredService<AuthStateProvider>(),
            services.GetRequiredService<HubConnectionFactory>(),
            NullLogger<TaskTrackerService>.Instance)
        {
            Clock = new FakeTimeProvider()
        });
    }

    [Fact]
    public async Task SplashLeaves_WhileTheRealtimeServerHasNotAnswered()
    {
        var cut = Render<MainLayout>();
        var tracker = Services.GetRequiredService<TaskTrackerService>();

        await cut.InvokeAsync(() => cut.Instance.OnViewportChanged(false));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.hideSplash");
            // The chrome is on screen while the hub is still connecting: the page did not wait for it.
            Assert.Contains("header-actions", cut.Markup, StringComparison.Ordinal);
            // Still the first attempt: none has failed. The negotiate's HTTP client gives up on its own
            // timeout and the tracker then retries at once, which a state check alone also reads as
            // connecting (measured: the unfixed layout passed that weaker check after 124 s).
            Assert.Equal(HubConnectionState.Connecting, tracker.ConnectionState);
            Assert.Null(tracker.LastFailureReason);
        }, BunitTestHelper.AsyncRenderWait);
    }

    [Fact]
    public async Task AStartTornDownBeforeTheServerAnswered_ReportsNoTimeout()
    {
        // With the start no longer awaited by the page, a sign-out or an account switch can tear the hub
        // down while its first attempt waits. Disposing cancels that attempt, which read as "did not
        // answer within 10 s" and was left as the reason of a connection that no longer exists.
        var tracker = Services.GetRequiredService<TaskTrackerService>();
        var start = tracker.StartAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(HubConnectionState.Connecting, tracker.ConnectionState);

        await tracker.StopAsync();
        await Aetheus.Front.Tests.TestDoubles.HangGuard.AwaitAsync(start, "the torn-down start returned");

        Assert.Null(tracker.LastFailureReason);
    }
}
