// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Shared;

/// <summary>
/// R-1: a zone that waits holds the application's ONE page veil through <see cref="VeilHold"/>, which counts with the
/// running actions of <see cref="BusyVeil"/>. Proves the hold: it raises the veil once, keeps it while any other holder or
/// action remains, and always gives it back (a flag turned off, the zone leaving the page, a handle disposed twice).
/// </summary>
public sealed class VeilHoldTests : BunitContext
{
    public VeilHoldTests() => BunitTestHelper.RegisterServices(this, isAdmin: true);

    [Fact]
    public void BusyVeil_Hold_RaisesTheVeilOnce_AndAHandleDisposedTwiceLiftsItOnce()
    {
        var veil = new BusyVeil();
        var changes = new List<bool>();
        veil.Changed += () => changes.Add(veil.IsBusy);

        var first = veil.Hold();
        var second = veil.Hold();
        Assert.True(veil.IsBusy);

        first.Dispose();
        first.Dispose();
        Assert.True(veil.IsBusy);

        second.Dispose();
        Assert.False(veil.IsBusy);
        Assert.Equal([true, true, true, false], changes);
    }

    [Fact]
    public async Task BusyVeil_Hold_CountsWithARunningAction()
    {
        var veil = new BusyVeil();
        var hold = veil.Hold();
        var release = new TaskCompletionSource();
        var running = veil.RunAsync(() => release.Task);

        release.SetResult();
        await running;
        Assert.True(veil.IsBusy);

        hold.Dispose();
        Assert.False(veil.IsBusy);
    }

    [Fact]
    public async Task TheComponent_HoldsTheVeilWhileBusy_ReleasesItWhenTheFlagFalls_AndWhenItLeavesThePage()
    {
        var veil = Services.GetRequiredService<BusyVeil>();

        var cut = Render<VeilHold>(parameters => parameters.Add(component => component.Busy, false));
        Assert.False(veil.IsBusy);

        cut.Render(parameters => parameters.Add(component => component.Busy, true));
        Assert.True(veil.IsBusy);

        // Rendered again while still busy: still one hold, not a second one.
        cut.Render(parameters => parameters.Add(component => component.Busy, true));
        cut.Render(parameters => parameters.Add(component => component.Busy, false));
        Assert.False(veil.IsBusy);

        cut.Render(parameters => parameters.Add(component => component.Busy, true));
        Assert.True(veil.IsBusy);
        await DisposeComponentsAsync();
        Assert.False(veil.IsBusy);
    }
}
