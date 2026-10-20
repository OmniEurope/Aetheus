// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Layout;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Layout;

/// <summary>Verifies connection-loss feedback when the initial backend connection fails. An
/// unauthenticated render avoids real hub retries while reflection exercises the layout's
/// orchestration state.</summary>
public class MainLayoutReconnexionTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type LayoutType = typeof(MainLayout);
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly FakeTimeProvider _time = new();

    public MainLayoutReconnexionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: false);
        Services.AddSingleton<TimeProvider>(_time);
        _handler.SetResponse(HttpMethod.Get, "health/live", System.Net.HttpStatusCode.ServiceUnavailable);
    }

    private static void SetField(object instance, string name, object? value) =>
        LayoutType.GetField(name, Priv)!.SetValue(instance, value);

    private static T GetField<T>(object instance, string name) =>
        (T)LayoutType.GetField(name, Priv)!.GetValue(instance)!;

    /// <summary>Dispatches a connection state change and returns its handling, which a drop keeps running
    /// through the grace delay (on the simulated clock) and the liveness probe: a test awaits it once it
    /// moved the clock, instead of waiting for the page to settle against the real one.</summary>
    private static async Task<Task> InvokeHandleConnectionStateChanged(IRenderedComponent<MainLayout> cut, bool connected)
    {
        var method = LayoutType.GetMethod("HandleConnectionStateChanged", Priv)!;
        var handling = Task.CompletedTask;
        await cut.InvokeAsync(() => { handling = (Task)method.Invoke(cut.Instance, [connected])!; });
        return handling;
    }

    private static Task HandledAsync(Task handling) =>
        Aetheus.Front.Tests.TestDoubles.HangGuard.AwaitAsync(handling, "the connection state change handled");

    // ── (a) drop -> grace -> dialog shown -> reconnect -> dialog closes ───────

    [Fact]
    public async Task Drop_AfterPriorConnect_ShowsDialogAfterGrace_ThenReconnectCloses()
    {
        var cut = Render<MainLayout>();

        // Simulate the classic path: a genuinely successful connect sets _wasConnectedOnce.
        await InvokeHandleConnectionStateChanged(cut, true);
        Assert.True(GetField<bool>(cut.Instance, "_wasConnectedOnce"));
        Assert.False(GetField<bool>(cut.Instance, "_showOfflineDialog"));

        // Drop: must NOT show immediately - the 2s grace period guards against a reload/blip flash.
        var drop = await InvokeHandleConnectionStateChanged(cut, false);
        Assert.False(GetField<bool>(cut.Instance, "_showOfflineDialog"));
        _time.Advance(TimeSpan.FromSeconds(2));
        await HandledAsync(drop);

        // After the grace period elapses, the dialog appears. Wait on the MARKUP (not just the field) so
        // the child ConnectionLostDialog has actually re-rendered with Visible=true before asserting.
        await cut.WhenRenderedAsync(() => Assert.Single(cut.FindAll(".omni-connection-overlay")));
        Assert.True(GetField<bool>(cut.Instance, "_showOfflineDialog"));

        // Reconnect: the dialog closes again, without a page reload.
        await InvokeHandleConnectionStateChanged(cut, true);
        Assert.False(GetField<bool>(cut.Instance, "_showOfflineDialog"));
        Assert.Empty(cut.FindAll(".omni-connection-overlay"));
    }

    // ── (b) drop-at-startup (never connected) still surfaces the dialog ───────

    [Fact]
    public async Task Drop_WithoutEverConnecting_StillArmsDialog_WhenConnectWasAttempted()
    {
        var cut = Render<MainLayout>();

        // Simulates an initial connection attempt that failed before reaching Connected.
        SetField(cut.Instance, "_connectAttempted", true);
        Assert.False(GetField<bool>(cut.Instance, "_wasConnectedOnce"));

        var drop = await InvokeHandleConnectionStateChanged(cut, false);
        Assert.False(GetField<bool>(cut.Instance, "_showOfflineDialog")); // still within the grace period
        _time.Advance(TimeSpan.FromSeconds(2));
        await HandledAsync(drop);

        await cut.WhenRenderedAsync(() => Assert.Single(cut.FindAll(".omni-connection-overlay")));
        Assert.True(GetField<bool>(cut.Instance, "_showOfflineDialog"));
    }

    [Fact]
    public async Task Drop_WithoutEverConnectingOrAttempting_DoesNotShowDialog()
    {
        // Sanity check for the opposite branch: a session that never attempted a connect at all
        // (e.g. never authenticated) must not show a spurious connection-lost dialog.
        var cut = Render<MainLayout>();

        Assert.False(GetField<bool>(cut.Instance, "_wasConnectedOnce"));
        Assert.False(GetField<bool>(cut.Instance, "_connectAttempted"));

        await HandledAsync(await InvokeHandleConnectionStateChanged(cut, false));

        Assert.False(GetField<bool>(cut.Instance, "_showOfflineDialog"));
        Assert.Empty(cut.FindAll(".omni-connection-overlay"));
    }

    [Fact]
    public async Task SignalRDrop_WithHealthyHttpBackend_DoesNotDeclareBackendOffline()
    {
        _handler.SetResponse(HttpMethod.Get, "health/live", System.Net.HttpStatusCode.OK);
        var cut = Render<MainLayout>();
        SetField(cut.Instance, "_connectAttempted", true);

        var drop = await InvokeHandleConnectionStateChanged(cut, false);
        _time.Advance(TimeSpan.FromSeconds(2));
        await HandledAsync(drop);

        await cut.WhenRenderedAsync(() =>
        {
            Assert.True(cut.Instance.BackendConnected);
            Assert.False(GetField<bool>(cut.Instance, "_showOfflineDialog"));
            Assert.Empty(cut.FindAll(".omni-connection-overlay"));
        });
    }

    [Fact]
    public async Task Reconnect_DuringTheLivenessProbe_DoesNotOpenTheOverlayAfterwards()
    {
        // Candidate 2444: the hub came back while the grace period's health/live probe was in
        // flight. Reconnecting cancels that probe, a cancelled probe answers "not live", and the
        // overlay opened on a connected page and stayed there.
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse<object>(HttpMethod.Get, "health/live", async ct =>
        {
            probeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new object();
        });
        var cut = Render<MainLayout>();
        SetField(cut.Instance, "_connectAttempted", true);

        var drop = await InvokeHandleConnectionStateChanged(cut, false);
        _time.Advance(TimeSpan.FromSeconds(2));
        await probeStarted.Task.WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, Xunit.TestContext.Current.CancellationToken);

        await HandledAsync(await InvokeHandleConnectionStateChanged(cut, true));
        // The reconnect cancelled the probe: the drop's handling ends there, having decided.
        await HandledAsync(drop);

        Assert.Null(GetField<CancellationTokenSource?>(cut.Instance, "_offlineDelayCts"));
        Assert.False(GetField<bool>(cut.Instance, "_showOfflineDialog"));
        Assert.Empty(cut.FindAll(".omni-connection-overlay"));
    }

    [Fact]
    public async Task Logout_CancelsPendingOfflineDialog_AndItStaysHidden()
    {
        var cut = Render<MainLayout>();
        SetField(cut.Instance, "_connectAttempted", true);

        await InvokeHandleConnectionStateChanged(cut, false);
        Assert.NotNull(GetField<CancellationTokenSource>(cut.Instance, "_offlineDelayCts"));

        await cut.InvokeAsync(() => cut.Instance.OnLogout());
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(GetField<bool>(cut.Instance, "_showOfflineDialog"));
        Assert.False(GetField<bool>(cut.Instance, "_connectAttempted"));
        Assert.Null(GetField<CancellationTokenSource?>(cut.Instance, "_offlineDelayCts"));
        Assert.Empty(cut.FindAll(".omni-connection-overlay"));
    }

    // ── OnManualReconnect with an expired session → redirect to /login ────

    [Fact]
    public async Task OnManualReconnect_SessionExpired_RedirectsToLogin()
    {
        var cut = Render<MainLayout>();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var method = LayoutType.GetMethod("OnManualReconnect", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.Contains("login", nav.Uri);
    }
}
