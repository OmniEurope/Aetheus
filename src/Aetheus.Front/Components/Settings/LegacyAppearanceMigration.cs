// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Settings;

/// <summary>
/// R1-66, OE 1.8.1: the appearance moved to OE's <see cref="OmniAppearanceState"/>
/// (<see cref="OmniAppearanceState.PreferenceKey"/>). For a signed-in user who has no OE appearance yet,
/// this writes the one Aetheus kept before: the former user preference (<see cref="PreferenceKeys.Appearance"/>),
/// else the former browser keys of this browser (<see cref="StorageKeys.Theme"/> and the others, removed
/// once copied so a second account does not receive them), else the Aetheus default, dark. The former
/// server preference is left in place, unread.
/// </summary>
public sealed class LegacyAppearanceMigration(OmniPreferences omni, IUserPreferences preferences, OmniAppearanceState appearance, IJSRuntime js)
{
    private static readonly string[] BrowserKeys =
    [
        StorageKeys.Theme, StorageKeys.ThemePreset, StorageKeys.ThemePalette, StorageKeys.ThemeFont,
        StorageKeys.Density, StorageKeys.TextSize, StorageKeys.ControlSize, StorageKeys.BackdropMotion
    ];

    /// <summary>Writes the former appearance when the user has none in OE; returns false when there was nothing to do.</summary>
    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        if (await omni.GetAsync<JsonElement?>(OmniAppearanceState.PreferenceKey, ct) is not null)
            return false;

        var former = await preferences.GetAsync<AppearancePreference>(PreferenceKeys.Appearance, ct)
            ?? await ReadBrowserAsync(ct);
        await ApplyAsync(former);
        if (former is not null)
            foreach (var key in BrowserKeys)
                await js.InvokeVoidAsync("localStorage.removeItem", ct, key);
        return true;
    }

    /// <summary>The Aetheus default before any choice: dark, the rest as OE draws it.</summary>
    public Task ApplyDefaultAsync() => appearance.SetAppearanceAsync(OmniAppearance.Dark);

    private async Task ApplyAsync(AppearancePreference? former)
    {
        await appearance.SetAppearanceAsync(SiteAppearance.ParseAppearance(former?.Theme));
        if (former is null)
            return;
        // The theme first: a new theme drops the palette and the font picked for the previous one.
        if (SiteAppearance.Preset(former.Preset) is { } preset)
            await appearance.SetPresetAsync(preset);
        if (SiteAppearance.Palette(former.Palette) is { } palette)
            await appearance.SetPaletteAsync(palette);
        if (SiteAppearance.Font(former.Font) is { } font)
            await appearance.SetFontAsync(font);
        await appearance.SetDensityAsync(SiteAppearance.ParseDensity(former.Density));
        await appearance.SetTextSizeLevelAsync(SiteAppearance.ParseLevel(former.TextSize));
        await appearance.SetControlSizeLevelAsync(SiteAppearance.ParseLevel(former.ControlSize));
        await appearance.SetBackdropMotionAsync(former.BackdropMotion != "off");
    }

    private async Task<AppearancePreference?> ReadBrowserAsync(CancellationToken ct)
    {
        var values = new string?[BrowserKeys.Length];
        for (var index = 0; index < BrowserKeys.Length; index++)
            values[index] = await js.InvokeAsync<string?>("localStorage.getItem", ct, BrowserKeys[index]);
        return values.All(string.IsNullOrEmpty)
            ? null
            : new AppearancePreference(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
    }

    /// <summary>The former Aetheus preference: the same text values as the former browser keys, one per key.</summary>
    internal sealed record AppearancePreference(
        string? Theme, string? Preset, string? Palette, string? Font,
        string? Density, string? TextSize, string? ControlSize, string? BackdropMotion);
}
