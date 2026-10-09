// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Settings;

/// <summary>
/// Recette R-390: the user menu's "Theme" opens OE's appearance window with the site's theme, palette,
/// text size, density and control size, read from and written to the same storage keys as the settings
/// page (SiteAppearanceState), which the application's OmniThemeScope paints at once.
/// </summary>
public sealed class SiteAppearanceWindowTests : BunitContext
{
    public SiteAppearanceWindowTests() => BunitTestHelper.RegisterServices(this);

    [Theory]
    [InlineData(OmniDensity.Compact, "compact")]
    [InlineData(OmniDensity.Comfortable, "comfortable")]
    [InlineData(OmniDensity.Spacious, "spacious")]
    public void Densities_AreStoredUnderTheNamesTheStylesheetKnows(OmniDensity density, string stored)
    {
        Assert.Equal(stored, SiteAppearance.StoredDensity(density));
        Assert.Equal(density, SiteAppearance.ParseDensity(stored));
    }

    [Fact]
    public async Task Open_ReadsTheStoredChoices_AndShowsEveryRowItApplies()
    {
        var theme = OmniThemePresets.All[1];
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.ThemePreset).SetResult(theme.Name);
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.ThemePalette).SetResult(string.Empty);
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.Density).SetResult("compact");
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.TextSize).SetResult("7");
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.ControlSize).SetResult("3");
        var cut = Render<SiteAppearanceWindow>();
        Assert.Empty(cut.FindAll(".omni-appearance-window"));

        await cut.InvokeAsync(cut.Instance.OpenAsync);

        var window = cut.Find(".omni-appearance-window");
        // Decision of 2026-10-06: the whole window as OE draws it, every row bound: theme, palette with the
        // mode beside it, font, density, text size, control size (the moving field only under Givre and
        // Trou noir, below).
        var rows = window.QuerySelectorAll(".omni-appearance-settings--window .omni-appearance-settings__row");
        Assert.Equal(6, rows.Length);
        Assert.Contains(rows, row => row.TextContent.Contains("7/10", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.TextContent.Contains("3/10", StringComparison.Ordinal));
        var densities = rows[3].QuerySelectorAll(".omni-select-bar__item");
        Assert.Equal(3, densities.Length);
        // No moving field under this theme, so no switch to hold it still.
        Assert.Empty(window.QuerySelectorAll(".omni-switch__track"));
        Assert.Contains("omni-select-bar__item--selected", densities[0].ClassList);
    }

    /// <summary>Decision of 2026-10-06: under a theme that moves its field the window offers to hold it
    /// still, and the choice is stored and handed to the scope.</summary>
    [Fact]
    public async Task UnderTrouNoir_TheMovingFieldCanBeHeldStill_AndTheChoiceIsStored()
    {
        var trouNoir = OmniThemePresets.All.Single(preset => preset.Name == "Trou noir");
        JSInterop.Setup<string?>("localStorage.getItem", StorageKeys.ThemePreset).SetResult(trouNoir.Name);
        var cut = Render<SiteAppearanceWindow>();
        await cut.InvokeAsync(cut.Instance.OpenAsync);

        var window = cut.Find(".omni-appearance-window");
        var rows = window.QuerySelectorAll(".omni-appearance-settings--window .omni-appearance-settings__row");
        Assert.Equal(6, rows.Length);
        // The switch sits on the theme row's title line, so it adds no row.
        Assert.NotNull(rows[0].QuerySelector(".omni-switch__track"));

        var state = Services.GetRequiredService<SiteAppearanceState>();
        await cut.InvokeAsync(() => state.SetBackdropMotionAsync(false));
        Assert.False(state.BackdropMotion);
        Assert.Contains(JSInterop.Invocations["localStorage.setItem"],
            call => (string?)call.Arguments[0] == StorageKeys.BackdropMotion && (string?)call.Arguments[1] == "off");
    }

    /// <summary>User decision 2026-09-29: no "Aetheus" theme of its own; the list is OE's, where the
    /// first catalogue theme (Essentiel) stands for the default.</summary>
    [Fact]
    public async Task TheThemeList_IsOesOwn_WithoutAnAetheusEntry()
    {
        var cut = Render<SiteAppearanceWindow>();
        await cut.InvokeAsync(cut.Instance.OpenAsync);

        var themes = cut.Find(".omni-appearance-window select").QuerySelectorAll("option").Select(option => option.TextContent).ToList();
        Assert.StartsWith(OmniThemePresets.All[0].Name, themes[0], StringComparison.Ordinal);
        Assert.Equal(OmniThemePresets.All.Count, themes.Count);
        Assert.DoesNotContain("Aetheus", themes);
    }

    [Fact]
    public async Task ChangingTheDensity_StoresAndAppliesItsBand()
    {
        var cut = Render<SiteAppearanceWindow>();
        await cut.InvokeAsync(cut.Instance.OpenAsync);

        // OE shows the three densities as a bar of choices: the third is Spacious.
        cut.FindAll(".omni-appearance-settings--window .omni-select-bar__item")[2].Click();

        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "localStorage.setItem"
            && Equals(call.Arguments[0], StorageKeys.Density) && Equals(call.Arguments[1], "spacious"));
        // STD-SHELL: the application's OmniThemeScope paints it from the shared state (AppThemeScopeTests).
        Assert.Equal(OmniDensity.Spacious, Services.GetRequiredService<SiteAppearanceState>().Density);
    }

    [Fact]
    public async Task ChangingTheTextAndControlSizes_StoresAndAppliesThem()
    {
        var cut = Render<SiteAppearanceWindow>();
        await cut.InvokeAsync(cut.Instance.OpenAsync);
        var sliders = cut.FindAll(".omni-appearance-settings--window input[type=range]");

        sliders[0].Input("8");
        cut.FindAll(".omni-appearance-settings--window input[type=range]")[1].Input("2");

        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "localStorage.setItem"
            && Equals(call.Arguments[0], StorageKeys.TextSize) && Equals(call.Arguments[1], "8"));
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "Aetheus.setTextSize"
            && Equals(call.Arguments[0], 8));
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "localStorage.setItem"
            && Equals(call.Arguments[0], StorageKeys.ControlSize) && Equals(call.Arguments[1], "2"));
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "Aetheus.setControlSize"
            && Equals(call.Arguments[0], 2));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("0", 5)]
    [InlineData("11", 5)]
    [InlineData("-3", 5)]
    [InlineData("x", 5)]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    public void AStoredScaleLevel_OutOfRange_ReadsAsDrawn(string? stored, int expected)
        => Assert.Equal(expected, SiteAppearance.ParseLevel(stored));
}
