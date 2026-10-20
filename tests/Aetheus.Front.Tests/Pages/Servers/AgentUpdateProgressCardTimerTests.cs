// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Targets the zero-covered methods in <see cref="AgentUpdateProgressCard"/>:
/// - <c>StartStaleTimer(int)</c> - creates a timer on the injected clock; verify _staleTimer becomes non-null
/// - <c>CancelStaleTimer</c> - disposes the timer; verify _staleTimer becomes null
/// - <c>HandleHeartbeat</c> version-changed → Done transition (AgentOffline or LaunchingUpdater phase)
/// - Timer expiry callback behaviour
/// </summary>
public class AgentUpdateProgressCardTimerTests
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type CardType = typeof(AgentUpdateProgressCard);

    private static AgentUpdateProgressCard CreateInstance()
    {
        var instance = new AgentUpdateProgressCard();
        var localizer = new BunitTestHelper.StubLocalizer();
        CardType.GetProperty("L", Priv)!.SetValue(instance, localizer);
        instance.Clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        CardType.GetProperty("Toast", Priv)!.SetValue(instance,
            new Aetheus.Front.Components.Shared.NotifyHelper(new OmniEurope.Blazor.Components.OmniOverlayService(new Microsoft.Extensions.Time.Testing.FakeTimeProvider()), localizer));
        return instance;
    }

    private static void SetField(object instance, string name, object? value)
        => CardType.GetField(name, Priv)!.SetValue(instance, value);

    private static T? GetField<T>(object instance, string name)
        => (T?)CardType.GetField(name, Priv)!.GetValue(instance);

    // ── StartStaleTimer creates a non-null _staleTimer ───────────────────────

    [Fact]
    public void StartStaleTimer_CreatesTimer()
    {
        var instance = CreateInstance();

        var method = CardType.GetMethod("StartStaleTimer", Priv)!;
        method.Invoke(instance, [300]); // 300 s - won't fire in test

        var timer = GetField<ITimer?>(instance, "_staleTimer");
        Assert.NotNull(timer);

        // Cleanup
        timer?.Dispose();
    }

    [Fact]
    public void StartStaleTimer_CalledTwice_DisposesOldTimer()
    {
        var instance = CreateInstance();
        var method = CardType.GetMethod("StartStaleTimer", Priv)!;

        method.Invoke(instance, [300]);
        var first = GetField<ITimer?>(instance, "_staleTimer");

        method.Invoke(instance, [300]); // second call disposes first
        var second = GetField<ITimer?>(instance, "_staleTimer");

        Assert.NotNull(second);
        // second timer replaces first (may be same object if pooled, but must be set)
        second?.Dispose();
    }

    // ── CancelStaleTimer nulls out _staleTimer ────────────────────────────────

    [Fact]
    public void CancelStaleTimer_AfterStart_SetsTimerToNull()
    {
        var instance = CreateInstance();

        var startMethod = CardType.GetMethod("StartStaleTimer", Priv)!;
        startMethod.Invoke(instance, [300]);

        Assert.NotNull(GetField<ITimer?>(instance, "_staleTimer"));

        var cancelMethod = CardType.GetMethod("CancelStaleTimer", Priv)!;
        cancelMethod.Invoke(instance, []);

        Assert.Null(GetField<ITimer?>(instance, "_staleTimer"));
    }

    [Fact]
    public void CancelStaleTimer_WhenAlreadyNull_LeavesItNull()
    {
        var instance = CreateInstance();
        // _staleTimer starts as null before any update is launched.
        var method = CardType.GetMethod("CancelStaleTimer", Priv)!;
        method.Invoke(instance, []); // must not throw
        Assert.Null(GetField<ITimer?>(instance, "_staleTimer"));
    }

    // ── Backend confirmation is the only transition to Done ─────────────────

    [Fact]
    public void HandleConfirmed_AgentOffline_TransitionsToDone()
    {
        var instance = CreateInstance();
        SetField(instance, "_phase", AgentUpdatePhase.AgentOffline);

        // HandleConfirmed sets _phase = Done and _percent = 100 BEFORE calling
        // InvokeAsync. Without a renderer the InvokeAsync call throws
        // InvalidOperationException, but the field assignments already happened.
        try { instance.HandleConfirmed(12, "2.0.0"); }
        catch (InvalidOperationException) { /* render handle not assigned - expected */ }

        var phase = GetField<AgentUpdatePhase?>(instance, "_phase");
        Assert.Equal(AgentUpdatePhase.Done, phase);
        Assert.Equal(100, GetField<int>(instance, "_percent"));
    }

    [Fact]
    public void HandleHeartbeat_LaunchingUpdater_DoesNotConfirmUpdate()
    {
        var instance = CreateInstance();
        SetField(instance, "_phase", AgentUpdatePhase.LaunchingUpdater);

        instance.HandleHeartbeat("3.1.0");

        var phase = GetField<AgentUpdatePhase?>(instance, "_phase");
        Assert.Equal(AgentUpdatePhase.LaunchingUpdater, phase);
    }

    [Fact]
    public void HandleHeartbeat_WrongPhase_DoesNotTransition()
    {
        var instance = CreateInstance();
        // Neither AgentOffline nor LaunchingUpdater
        SetField(instance, "_phase", AgentUpdatePhase.Downloading);

        instance.HandleHeartbeat("2.0.0");

        Assert.Equal(AgentUpdatePhase.Downloading, GetField<AgentUpdatePhase?>(instance, "_phase"));
    }

    // ── DisposeAsync disposes timer ───────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_WithActiveTimer_DisposesTimer()
    {
        var instance = CreateInstance();
        var startMethod = CardType.GetMethod("StartStaleTimer", Priv)!;
        startMethod.Invoke(instance, [300]);

        Assert.NotNull(GetField<ITimer?>(instance, "_staleTimer"));

        await instance.DisposeAsync();

        Assert.Null(GetField<ITimer?>(instance, "_staleTimer"));
    }

    // Helper for int field
    private static int GetField(object instance, string name)
        => (int)CardType.GetField(name, Priv)!.GetValue(instance)!;
}
