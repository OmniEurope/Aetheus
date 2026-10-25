// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Layout;

/// <summary>
/// The "synchronising" notification the user sees on picking the phone back up during a run: shown
/// while the realtime side catches up, gone once it has, never next to the connection-lost overlay and
/// never stuck. The page's visibility reaches it the way the browser sends it, through the JS callback.
/// </summary>
public sealed class RealtimeSyncIndicatorTests : BunitContext
{
    private const string Indicator = "[data-testid='realtime-sync-indicator']";
    private readonly FakeTimeProvider _time = new();
    private readonly RealtimeSyncStatus _status;

    public RealtimeSyncIndicatorTests()
    {
        BunitTestHelper.RegisterServices(this);
        _status = new RealtimeSyncStatus(_time);
        Services.AddSingleton<HubConnectionFactory>(sp => new HubConnectionFactory(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<AuthStateProvider>(),
            NullLogger<AuthDelegatingHandler>.Instance,
            _status));
    }

    [Fact]
    public void Resume_WhileAHubCatchesUp_ShowsTheNotification_AndHidesItOnceRefetched()
    {
        var cut = Render<RealtimeSyncIndicator>();

        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(false));
        var catchUp = _status.BeginCatchUp();
        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(true));

        cut.WaitForAssertion(() => Assert.Contains("RealtimeSyncInProgress", cut.Find(Indicator).TextContent));
        // A full veil with the animated plane, announced, nothing to dismiss.
        var veil = cut.Find(Indicator);
        Assert.Equal("status", veil.GetAttribute("role"));
        Assert.Equal("true", veil.GetAttribute("aria-busy"));
        Assert.Single(cut.FindComponents<AetheusLoader>());
        Assert.Empty(cut.FindAll(".omni-notification"));

        catchUp.Dispose();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(Indicator)));
    }

    [Fact]
    public void QuickResume_WithEverythingConnected_ShowsNothing()
    {
        var cut = Render<RealtimeSyncIndicator>();

        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(false));
        _time.Advance(TimeSpan.FromSeconds(2));
        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(true));
        _time.Advance(RealtimeSyncStatus.SettleWindow);

        Assert.Empty(cut.FindAll(Indicator));
    }

    [Fact]
    public void OfflineOverlay_TakesOver_AndTheNotificationIsHidden()
    {
        var cut = Render<RealtimeSyncIndicator>();
        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(false));
        using var catchUp = _status.BeginCatchUp();
        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(true));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(Indicator)));

        cut.Render(parameters => parameters.Add(p => p.OfflineOverlayShown, true));

        Assert.Empty(cut.FindAll(Indicator));
    }

    [Fact]
    public void CatchUp_ThatNeverReportsItsEnd_IsHiddenAfterTheSafetyTimeout()
    {
        var cut = Render<RealtimeSyncIndicator>();
        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(false));
        var stuck = _status.BeginCatchUp();
        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(true));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(Indicator)));

        _time.Advance(RealtimeSyncStatus.SafetyTimeout);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(Indicator)));
        stuck.Dispose();
    }

    // R1-42: the visibility the browser sends also reaches the timeline the outage reports read.
    [Fact]
    public void PageVisibilityChanges_FeedTheTimelineOfTheOutageReports()
    {
        var cut = Render<RealtimeSyncIndicator>();
        var timeline = Services.GetRequiredService<HubConnectionFactory>().PageVisibility;
        var now = DateTimeOffset.UtcNow;
        var later = (From: now.AddHours(1), To: now.AddHours(2));

        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(false));
        // Still hidden: the open stretch covers any later window.
        Assert.True(timeline.HiddenBetween(later.From, later.To) > TimeSpan.Zero);

        cut.InvokeAsync(() => cut.Instance.OnPageVisibilityChanged(true));
        // Shown again: the stretch is closed and no longer reaches a later window.
        Assert.Equal(TimeSpan.Zero, timeline.HiddenBetween(later.From, later.To));
    }

    [Fact]
    public void Render_RegistersThePageVisibilityWatcher()
    {
        Render<RealtimeSyncIndicator>();

        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.watchPageVisibility");
    }
}
