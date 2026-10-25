// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Settings;

/// <summary> The user menu's "Theme" opens OE's appearance window with the site's theme, palette,
/// text size, density and control size, read from and written to OE's OmniAppearanceState (the user
/// preference omni.appearance, R1-66), the same source as the settings page, which the application's
/// OmniThemeScope paints at once.
/// </summary>
public sealed class SiteAppearanceWindowTests : BunitContext
{
    public SiteAppearanceWindowTests() => BunitTestHelper.RegisterServices(this);

    private OmniAppearanceState State => Services.GetRequiredService<OmniAppearanceState>();

    [Theory]
    [InlineData(OmniDensity.Compact, "compact")]
    [InlineData(OmniDensity.Comfortable, "comfortable")]
    [InlineData(OmniDensity.Spacious, "spacious")]
    public void FormerDensities_ReadBackUnderTheNamesAetheusStored(OmniDensity density, string stored) =>
        Assert.Equal(density, SiteAppearance.ParseDensity(stored));

    [Fact]
    public async Task Open_ShowsTheUsersAppearance_AndEveryRowAetheusApplies_ContentWidthIncluded()
    {
        var theme = OmniThemePresets.All[1];
        await State.SetPresetAsync(theme);
        await State.SetDensityAsync(OmniDensity.Compact);
        await State.SetTextSizeLevelAsync(7);
        await State.SetControlSizeLevelAsync(3);
        var cut = Render<SiteAppearanceWindow>();
        Assert.Empty(cut.FindAll(".omni-appearance-window"));

        await cut.InvokeAsync(cut.Instance.OpenAsync);

        var window = cut.Find(".omni-appearance-window");
        // Decision of 2026-10-06: theme, palette with the mode beside it, font, density, text size, control
        // size (the moving field only under Givre and Trou noir, below). R1-25 (2026-10-21): the content
        // width row (centred or full width) is shown too, since the window binds FullWidthChanged.
        var rows = window.QuerySelectorAll(".omni-appearance-settings--window .omni-appearance-settings__row");
        Assert.Equal(7, rows.Length);
        Assert.Contains(rows, row => row.TextContent.Contains("7/10", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.TextContent.Contains("3/10", StringComparison.Ordinal));
        var densities = rows[3].QuerySelectorAll(".omni-select-bar__item");
        Assert.Equal(3, densities.Length);
        Assert.Contains("omni-select-bar__item--selected", densities[0].ClassList);
    }

    /// <summary>R1-25: the window offers the content width, shows the state's value and keeps the choice
    /// in the user's appearance, which the main area of the shell follows.</summary>
    [Fact]
    public async Task TheContentWidth_IsOffered_ShowsTheStateValue_AndTheChoiceIsKept()
    {
        await State.SetFullWidthAsync(true);
        var cut = Render<SiteAppearanceWindow>();
        await cut.InvokeAsync(cut.Instance.OpenAsync);

        var window = cut.FindComponent<OmniAppearanceWindow>().Instance;
        Assert.True(window.FullWidthChanged.HasDelegate);
        Assert.True(window.FullWidth);

        await cut.InvokeAsync(() => window.FullWidthChanged.InvokeAsync(false));

        Assert.False(State.FullWidth);
        Assert.False(cut.FindComponent<OmniAppearanceWindow>().Instance.FullWidth);
    }

    /// <summary>Decision of 2026-10-06: under a theme that moves its field the window offers to hold it
    /// still, and the choice is kept in the user's appearance.</summary>
    [Fact]
    public async Task UnderTrouNoir_TheMovingFieldCanBeHeldStill_AndTheChoiceIsKept()
    {
        await State.SetPresetAsync(OmniThemePresets.All.Single(preset => preset.Name == "Trou noir"));
        var cut = Render<SiteAppearanceWindow>();
        await cut.InvokeAsync(cut.Instance.OpenAsync);

        var window = cut.Find(".omni-appearance-window");
        var rows = window.QuerySelectorAll(".omni-appearance-settings--window .omni-appearance-settings__row");
        Assert.Equal(7, rows.Length);
        // The switch sits on the theme row's title line, so it adds no row.
        var track = rows[0].QuerySelector(".omni-switch__track");
        Assert.NotNull(track);

        await cut.InvokeAsync(() => cut.FindComponent<OmniAppearanceWindow>().Instance.BackdropMotionChanged.InvokeAsync(false));

        Assert.False(State.BackdropMotion);
    }

    /// <summary>Use the OE theme catalogue, with Essentiel as the default and no
    /// application-specific theme.</summary>
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
    public async Task ChangingTheDensity_KeepsItInTheUsersAppearance()
    {
        var cut = Render<SiteAppearanceWindow>();
        await cut.InvokeAsync(cut.Instance.OpenAsync);

        // OE shows the three densities as a bar of choices: the third is Spacious.
        cut.FindAll(".omni-appearance-settings--window .omni-select-bar__item")[2].Click();

        // STD-SHELL: the application's OmniThemeScope paints it from the shared state (AppThemeScopeTests).
        Assert.Equal(OmniDensity.Spacious, State.Density);
    }

    [Fact]
    public async Task ChangingTheTextAndControlSizes_KeepsThemInTheUsersAppearance()
    {
        var cut = Render<SiteAppearanceWindow>();
        await cut.InvokeAsync(cut.Instance.OpenAsync);

        cut.FindAll(".omni-appearance-settings--window input[type=range]")[0].Input("8");
        cut.FindAll(".omni-appearance-settings--window input[type=range]")[1].Input("2");

        // The theme scope that follows the state sets data-oe-text-size and data-oe-control-size itself.
        Assert.Equal(8, State.TextSizeLevel);
        Assert.Equal(2, State.ControlSizeLevel);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier is "Aetheus.setTextSize" or "Aetheus.setControlSize");
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
    public void AFormerScaleLevel_OutOfRange_ReadsAsDrawn(string? stored, int expected)
        => Assert.Equal(expected, SiteAppearance.ParseLevel(stored));
}
