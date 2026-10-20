// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Layout;

/// <summary>STD-SHELL with OE 1.8.1 (R1-66): the real App's OmniThemeScope binds no appearance parameter and
/// follows OE's OmniAppearanceState, the user's appearance preference; it keeps the boot snapshot under
/// the key omni-boot.js replays before the first paint.</summary>
public sealed class AppThemeScopeTests : BunitContext
{
    public AppThemeScopeTests()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: false);
        handler.SetResponse(HttpMethod.Get, "health/live", System.Net.HttpStatusCode.ServiceUnavailable);
        Services.AddScoped(sp => new TaskTrackerService(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<AuthStateProvider>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            NullLogger<TaskTrackerService>.Instance));
    }

    private OmniAppearanceState State => Services.GetRequiredService<OmniAppearanceState>();

    /// <summary>Signed out, nothing chosen: the scope paints OE's shipped look in dark mode, the
    /// application's default, and binds nothing itself.</summary>
    [Fact]
    public void SignedOut_TheScopeIsDarkWithTheShippedLook_AndFollowsTheState()
    {
        var cut = Render<App>();

        cut.WaitForAssertion(() => Assert.Equal("dark", cut.Find("#app-theme-scope").GetAttribute("data-omni-theme")));
        Assert.Equal(OmniAppearance.Dark, State.Appearance);
        Assert.Null(State.Preset);
        Assert.Equal("comfortable", cut.Find("#app-theme-scope").GetAttribute("data-omni-density"));
        // OE keeps the boot snapshot itself, under the key omni-boot.js reads in index.html.
        Assert.Equal("aetheus:theme", cut.FindComponent<OmniThemeScope>().Instance.SnapshotKey);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier is "Aetheus.setOmniTheme" or "Aetheus.followThemeScope");
    }

    /// <summary>A change made in the Theme window, the settings page or received for the user repaints the
    /// scope at once.</summary>
    [Fact]
    public async Task AChangeOfTheUsersAppearance_ReachesTheScope()
    {
        var cut = Render<App>();
        cut.WaitForAssertion(() => Assert.Equal("dark", cut.Find("#app-theme-scope").GetAttribute("data-omni-theme")));

        await cut.InvokeAsync(() => State.SetAppearanceAsync(OmniAppearance.Light));
        await cut.InvokeAsync(() => State.SetDensityAsync(OmniDensity.Compact));

        cut.WaitForAssertion(() =>
        {
            var element = cut.Find("#app-theme-scope");
            Assert.Equal("light", element.GetAttribute("data-omni-theme"));
            Assert.Equal("compact", element.GetAttribute("data-omni-density"));
            // The scope carries the resolved mode; nothing is copied on the document root by Aetheus.
            Assert.Equal("light", element.GetAttribute("data-omni-theme-resolved"));
        });
    }
}
