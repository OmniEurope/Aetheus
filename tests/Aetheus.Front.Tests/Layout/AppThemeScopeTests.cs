// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Layout;

/// <summary>
/// STD-SHELL (user decision 2026-10-04): the application is wrapped in OE's OmniThemeScope, which paints the
/// look stored in this browser. These tests render the real App and read what its scope received.
/// </summary>
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

    [Fact]
    public void TheScope_ReceivesTheStoredModeThemePaletteFontAndDensity()
    {
        var theme = OmniThemePresets.All.First(preset => !preset.DarkOnly && preset != OmniThemePresets.All[0]);
        var palette = OmniThemePalettes.All[^1];
        var font = OmniThemeFonts.All[^1];
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.Theme).SetResult("light");
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.ThemePreset).SetResult(theme.Name);
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.ThemePalette).SetResult(palette.Name);
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.ThemeFont).SetResult(font.Name);
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.Density).SetResult("compact");

        var cut = Render<App>();

        cut.WaitForAssertion(() =>
        {
            var scope = cut.FindComponent<OmniThemeScope>().Instance;
            Assert.Equal(OmniAppearance.Light, scope.Appearance);
            Assert.Same(theme, scope.Preset);
            Assert.Same(palette, scope.Palette);
            Assert.Same(font, scope.Font);
            Assert.Equal(OmniDensity.Compact, scope.Density);
        });
        var element = cut.Find("#app-theme-scope");
        Assert.Equal("light", element.GetAttribute("data-omni-theme"));
        Assert.Equal("compact", element.GetAttribute("data-omni-density"));
        // The document root carries the same mode, for the boot splash and the rules keyed on the dark half.
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "Aetheus.setOmniTheme" && Equals(call.Arguments[0], "light"));
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "Aetheus.followThemeScope" && Equals(call.Arguments[0], "app-theme-scope"));
    }

    /// <summary>Nothing stored: the scope paints OE's shipped look in dark mode, the application's default.</summary>
    [Fact]
    public void NothingStored_TheScopeIsDarkWithTheShippedLook()
    {
        var cut = Render<App>();

        var scope = cut.FindComponent<OmniThemeScope>().Instance;
        Assert.Equal(OmniAppearance.Dark, scope.Appearance);
        Assert.Null(scope.Preset);
        Assert.Null(scope.Palette);
        Assert.Null(scope.Font);
        Assert.Equal(OmniDensity.Comfortable, scope.Density);
        Assert.Equal("dark", cut.Find("#app-theme-scope").GetAttribute("data-omni-theme"));
    }

    /// <summary>A change made in the Theme window or the settings page repaints the scope at once.</summary>
    [Fact]
    public async Task AChangeOfTheLook_ReachesTheScope()
    {
        var cut = Render<App>();
        var state = Services.GetRequiredService<Aetheus.Front.Components.Settings.SiteAppearanceState>();
        var theme = OmniThemePresets.All[^1];

        await cut.InvokeAsync(() => state.SetPresetAsync(theme));
        await cut.InvokeAsync(() => state.SetDensityAsync(OmniDensity.Spacious));
        await cut.InvokeAsync(() => state.SetAppearanceAsync(OmniAppearance.System));

        cut.WaitForAssertion(() =>
        {
            var scope = cut.FindComponent<OmniThemeScope>().Instance;
            Assert.Same(theme, scope.Preset);
            Assert.Equal(OmniDensity.Spacious, scope.Density);
            Assert.Equal(OmniAppearance.System, scope.Appearance);
        });
    }
}
