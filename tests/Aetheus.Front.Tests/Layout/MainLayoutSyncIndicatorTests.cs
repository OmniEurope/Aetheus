// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Layout;

/// <summary>
/// The layout hands its connection-lost overlay state to the synchronising notification, so the two are
/// never on screen together: the overlay stays the authority on a real outage.
/// </summary>
public sealed class MainLayoutSyncIndicatorTests : BunitContext
{
    public MainLayoutSyncIndicatorTests()
    {
        BunitTestHelper.RegisterServices(this);
        // The test hub never connects and the backend answers "not live", so on the wall clock the
        // layout's 2 second grace opened the real overlay by itself whenever a loaded machine took
        // longer than that to reach the first assertion. A clock that does not move keeps the overlay
        // in the hands of the test.
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
    }

    [Fact]
    public async Task SignedInLayout_RendersTheIndicator_AndPassesItTheOverlayState()
    {
        var cut = Render<MainLayout>();
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<RealtimeSyncIndicator>().Instance.OfflineOverlayShown));

        await cut.InvokeAsync(() => typeof(MainLayout)
            .GetField("_showOfflineDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(cut.Instance, true));
        cut.Render();

        Assert.True(cut.FindComponent<RealtimeSyncIndicator>().Instance.OfflineOverlayShown);
    }
}
