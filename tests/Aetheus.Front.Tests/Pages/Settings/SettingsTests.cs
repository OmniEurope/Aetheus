// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SettingsPage = Aetheus.Front.Components.Settings.Settings;

namespace Aetheus.Front.Tests.Pages;

public class SettingsTests : BunitContext
{
    public SettingsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: false);
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/users/me",
            new Aetheus.Shared.Components.Users.UserDto
            {
                Id = 1,
                Username = "testuser",
                IsActive = true,
                Roles = ["User"]
            });
    }

    private readonly BunitTestHelper.TestHandler _handler;

    [Fact]
    public void Renders_Settings_Page()
    {
        var cut = Render<SettingsPage>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        Assert.Contains("Settings", cut.Markup);
    }

    [Fact]
    public void Renders_ForNonAdmin()
    {
        var cut = Render<SettingsPage>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        // Settings is not admin-gated - a non-admin user still gets the full settings page.
        Assert.Contains("Settings", cut.Markup);
    }

    [Fact]
    public async Task AppearanceAndLanguage_PersistImmediately()
    {
        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "appearance"));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // The mode is OE's appearance settings (Light, Dark, System); the language stays a dropdown
        // in its own section.
        var settings = cut.FindComponent<OmniAppearanceSettings>();
        var language = cut.FindComponents<OmniDropDown<string>>()
            .Single(selector => selector.Instance.Id == "oe-formfield-pages-settings-settings-3");

        await cut.InvokeAsync(() => settings.Instance.AppearanceChanged.InvokeAsync(OmniAppearance.Light));
        await cut.InvokeAsync(() => language.Instance.ValueChanged.InvokeAsync("fr-FR"));

        var keys = JSInterop.Invocations
            .Where(i => i.Identifier == "localStorage.setItem")
            .Select(i => (string?)i.Arguments[0])
            .ToList();
        // The mode is the user's appearance in OE (omni.appearance); the language keeps its boot cache key.
        Assert.Equal(OmniAppearance.Light, Services.GetRequiredService<OmniAppearanceState>().Appearance);
        Assert.DoesNotContain("aetheus_theme", keys);
        Assert.Contains("aetheus_lang", keys);
        // The theme scope owns the mode; it is not copied to the document root.
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "Aetheus.setOmniTheme");
    }

    /// <summary>The settings page reuses OE's appearance settings, every row bound to what the site
    /// stores and applies: theme, palette, font and density go to the application's theme scope,
    /// the text and control sizes to their level on the document root.</summary>
    [Fact]
    public async Task OesAppearanceSettings_ApplyEveryChoiceToTheWholeSite()
    {
        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "appearance"));
        cut.WaitForState(() => cut.Markup.Length > 50);
        var settings = cut.FindComponent<OmniAppearanceSettings>().Instance;
        var theme = OmniThemePresets.All[1];
        var palette = OmniThemePalettes.All[0];
        var font = OmniThemeFonts.All[^1];

        Assert.True(settings.ControlSizeLevelChanged.HasDelegate);
        await cut.InvokeAsync(() => settings.PresetChanged.InvokeAsync(theme));
        await cut.InvokeAsync(() => settings.PaletteChanged.InvokeAsync(palette));
        await cut.InvokeAsync(() => settings.FontChanged.InvokeAsync(font));
        await cut.InvokeAsync(() => settings.DensityChanged.InvokeAsync(OmniDensity.Compact));
        await cut.InvokeAsync(() => settings.TextSizeLevelChanged.InvokeAsync(7));
        await cut.InvokeAsync(() => settings.ControlSizeLevelChanged.InvokeAsync(4));
        // R1-25: the content width (centred or full width) is offered here as in the Theme window.
        Assert.True(settings.FullWidthChanged.HasDelegate);
        await cut.InvokeAsync(() => settings.FullWidthChanged.InvokeAsync(false));
        Assert.False(Services.GetRequiredService<OmniAppearanceState>().FullWidth);

        // STD-SHELL: every choice is the user's appearance in OE's OmniAppearanceState (omni.appearance),
        // which the application's OmniThemeScope follows; nothing is written by hand, in storage or on the root.
        var state = Services.GetRequiredService<OmniAppearanceState>();
        Assert.Same(theme, state.Preset);
        Assert.Same(palette, state.Palette);
        Assert.Same(font, state.Font);
        Assert.Equal(OmniDensity.Compact, state.Density);
        Assert.Equal(7, state.TextSizeLevel);
        Assert.Equal(4, state.ControlSizeLevel);
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier is "Aetheus.setThemeTokens" or "Aetheus.setDensity" or "Aetheus.setTextSize" or "Aetheus.setControlSize");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "localStorage.setItem" && ((string?)i.Arguments[0])?.StartsWith("aetheus_theme", StringComparison.Ordinal) == true);

        // A new theme drops the palette and the font picked for the previous one.
        await cut.InvokeAsync(() => settings.PresetChanged.InvokeAsync(null));
        Assert.Null(state.Preset);
        Assert.Null(state.Palette);
        Assert.Null(state.Font);
    }

    /// <summary>The settings page and the Theme window read one source, so a change made in one
    /// shows in the other.</summary>
    [Fact]
    public async Task TheSettingsPageAndTheThemeWindow_ShareOneSource()
    {
        var page = Render<SettingsPage>(p => p.Add(x => x.Tab, "appearance"));
        page.WaitForState(() => page.Markup.Length > 50);
        var window = Render<Aetheus.Front.Components.Settings.SiteAppearanceWindow>();

        await page.InvokeAsync(() => page.FindComponent<OmniAppearanceSettings>().Instance.TextSizeLevelChanged.InvokeAsync(8));

        Assert.Equal(8, window.FindComponent<OmniAppearanceWindow>().Instance.TextSizeLevel);
    }
}
