// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Settings;

/// <summary>
/// R1-66, OE 1.8.1: the appearance moved to OE's <see cref="OmniAppearanceState"/>
/// (<see cref="OmniAppearanceState.PreferenceKey"/>). For a signed-in user who has no OE appearance yet,
/// this writes the one Aetheus kept before: the former user preference (<see cref="PreferenceKeys.Appearance"/>,
/// stored on the server for that very user), else the Aetheus default, the system theme (R-42). R-42: the former browser keys
/// (<see cref="StorageKeys.Theme"/> and the others) are never a source, since they belong to whoever used the
/// browser before, not to the account that signs in; they are removed so none later receives them. The former
/// server preference is left in place, unread.
/// </summary>
public sealed class LegacyAppearanceMigration(OmniPreferences omni, IUserPreferences preferences, OmniAppearanceState appearance, IJSRuntime js)
{
    /// <summary>The member of OE's stored appearance that holds the content width (camel case).</summary>
    private const string FullWidthMember = "fullWidth";

    private static readonly string[] BrowserKeys =
    [
        StorageKeys.Theme, StorageKeys.ThemePreset, StorageKeys.ThemePalette, StorageKeys.ThemeFont,
        StorageKeys.Density, StorageKeys.TextSize, StorageKeys.ControlSize, StorageKeys.BackdropMotion
    ];

    /// <summary>Writes the former appearance when the user has none in OE; returns false when there was nothing to do.</summary>
    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        if (await omni.GetAsync<JsonElement?>(OmniAppearanceState.PreferenceKey, ct) is { } stored)
        {
            // R1-25: OE draws the centred column by default, Aetheus is full width; an appearance saved
            // before the content width could be chosen has no such member and keeps the full width.
            if (stored.ValueKind == JsonValueKind.Object && !stored.TryGetProperty(FullWidthMember, out _))
                await appearance.SetFullWidthAsync(true);
            return false;
        }

        // R-42: only the former preference stored for this very user. The former browser keys carry no user: they
        // belong to whoever used this browser before, so a new account must never receive them.
        var former = await preferences.GetAsync<AppearancePreference>(PreferenceKeys.Appearance, ct);
        await ApplyAsync(former);
        foreach (var key in BrowserKeys)
            await js.InvokeVoidAsync("localStorage.removeItem", ct, key);
        return true;
    }

    /// <summary>The Aetheus default before any choice (R-42): follow the system theme, the rest as OE draws it.</summary>
    public async Task ApplyDefaultAsync()
    {
        await appearance.SetAppearanceAsync(OmniAppearance.System);
        await appearance.SetFullWidthAsync(true);
    }

    private async Task ApplyAsync(AppearancePreference? former)
    {
        // R-42: an account that never chose a look follows the system theme.
        await appearance.SetAppearanceAsync(former is null ? OmniAppearance.System : SiteAppearance.ParseAppearance(former.Theme));
        await appearance.SetFullWidthAsync(true);
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

    /// <summary>The former Aetheus preference: the same text values as the former browser keys, one per key.</summary>
    internal sealed record AppearancePreference(
        string? Theme, string? Preset, string? Palette, string? Font,
        string? Density, string? TextSize, string? ControlSize, string? BackdropMotion);
}
