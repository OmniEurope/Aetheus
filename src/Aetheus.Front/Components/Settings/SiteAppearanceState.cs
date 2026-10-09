// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Settings;

/// <summary>
/// Recette R-390 / R-448, STD-SHELL: the one source of the site's look, shared by the settings page
/// (<c>OmniAppearanceSettings</c>), the user menu's Theme window (<c>OmniAppearanceWindow</c>) and the
/// <c>OmniThemeScope</c> that wraps the application in <c>App.razor</c>. It reads the choices stored in this
/// browser and stores each change; the scope paints the mode, the theme, the palette, the font and the density
/// from these values (OmniEurope.Blazor owns the tokens). Two things stay with the site because OE leaves them
/// to the host: the text size and the control size levels (<c>data-oe-text-size</c>,
/// <c>data-oe-control-size</c> on the document root, which OE's stylesheet reads), and the resolved mode
/// mirrored on the document root (<see cref="RootMode"/>) for what lives outside the scope (the boot splash,
/// the page behind it) and the stylesheet rules keyed on the light or dark half.
/// </summary>
public sealed class SiteAppearanceState(IJSRuntime js)
{
    /// <summary>Raised after any change, so the scope and every surface showing the same values redraw.</summary>
    public event Action? Changed;

    public OmniAppearance Appearance { get; private set; } = OmniAppearance.Dark;
    public OmniThemePreset? Preset { get; private set; }
    public OmniThemePalette? Palette { get; private set; }
    public OmniThemeFont? Font { get; private set; }
    public OmniDensity Density { get; private set; } = OmniDensity.Comfortable;
    public int TextSizeLevel { get; private set; } = SiteAppearance.DefaultLevel;
    public int ControlSizeLevel { get; private set; } = SiteAppearance.DefaultLevel;

    /// <summary>Whether a theme that moves its field (Givre, Trou noir) may move it; on unless the user
    /// turned it off. A system asking for less motion holds the field still whatever this says.</summary>
    public bool BackdropMotion { get; private set; } = true;

    /// <summary>
    /// The mode mirrored on the document root: the stored preference (<c>system</c> is resolved by the
    /// browser), except under a theme drawn in dark only, which the scope always paints dark.
    /// </summary>
    internal string RootMode => Preset?.DarkOnly == true ? "dark" : StoredAppearance(Appearance);

    /// <summary>Reads the choices stored in this browser and mirrors the mode on the document root.</summary>
    public async Task LoadAsync()
    {
        Appearance = ParseAppearance(await ReadAsync(StorageKeys.Theme));
        Preset = SiteAppearance.Preset(await ReadAsync(StorageKeys.ThemePreset));
        Palette = SiteAppearance.Palette(await ReadAsync(StorageKeys.ThemePalette));
        Font = SiteAppearance.Font(await ReadAsync(StorageKeys.ThemeFont));
        Density = SiteAppearance.ParseDensity(await ReadAsync(StorageKeys.Density));
        TextSizeLevel = SiteAppearance.ParseLevel(await ReadAsync(StorageKeys.TextSize));
        ControlSizeLevel = SiteAppearance.ParseLevel(await ReadAsync(StorageKeys.ControlSize));
        BackdropMotion = await ReadAsync(StorageKeys.BackdropMotion) != "off";
        await MirrorRootModeAsync();
        Changed?.Invoke();
    }

    public async Task SetAppearanceAsync(OmniAppearance appearance)
    {
        Appearance = appearance;
        await StoreAsync(StorageKeys.Theme, StoredAppearance(appearance));
        await MirrorRootModeAsync();
        Changed?.Invoke();
    }

    /// <summary>A new theme comes with its own palette and font: the ones picked for the previous theme
    /// are dropped, as OmniAppearanceSettings does.</summary>
    public async Task SetPresetAsync(OmniThemePreset? preset)
    {
        Preset = preset;
        Palette = null;
        Font = null;
        await StoreAsync(StorageKeys.ThemePreset, preset?.Name ?? string.Empty);
        await StoreAsync(StorageKeys.ThemePalette, string.Empty);
        await StoreAsync(StorageKeys.ThemeFont, string.Empty);
        await MirrorRootModeAsync();
        Changed?.Invoke();
    }

    public async Task SetPaletteAsync(OmniThemePalette? palette)
    {
        Palette = palette;
        await StoreAsync(StorageKeys.ThemePalette, palette?.Name ?? string.Empty);
        Changed?.Invoke();
    }

    public async Task SetFontAsync(OmniThemeFont? font)
    {
        Font = font;
        await StoreAsync(StorageKeys.ThemeFont, font?.Name ?? string.Empty);
        Changed?.Invoke();
    }

    public async Task SetDensityAsync(OmniDensity density)
    {
        Density = density;
        await StoreAsync(StorageKeys.Density, SiteAppearance.StoredDensity(density));
        Changed?.Invoke();
    }

    public async Task SetTextSizeLevelAsync(int level)
    {
        TextSizeLevel = Math.Clamp(level, SiteAppearance.MinimumLevel, SiteAppearance.MaximumLevel);
        await StoreAsync(StorageKeys.TextSize, SiteAppearance.StoredLevel(TextSizeLevel));
        await js.InvokeVoidAsync("Aetheus.setTextSize", TextSizeLevel);
        Changed?.Invoke();
    }

    public async Task SetControlSizeLevelAsync(int level)
    {
        ControlSizeLevel = Math.Clamp(level, SiteAppearance.MinimumLevel, SiteAppearance.MaximumLevel);
        await StoreAsync(StorageKeys.ControlSize, SiteAppearance.StoredLevel(ControlSizeLevel));
        await js.InvokeVoidAsync("Aetheus.setControlSize", ControlSizeLevel);
        Changed?.Invoke();
    }

    public async Task SetBackdropMotionAsync(bool motion)
    {
        BackdropMotion = motion;
        await StoreAsync(StorageKeys.BackdropMotion, motion ? "on" : "off");
        Changed?.Invoke();
    }

    private ValueTask MirrorRootModeAsync() => js.InvokeVoidAsync("Aetheus.setOmniTheme", RootMode);

    private ValueTask StoreAsync(string key, string value) => js.InvokeVoidAsync("localStorage.setItem", key, value);

    private ValueTask<string?> ReadAsync(string key) => js.InvokeAsync<string?>("localStorage.getItem", key);

    /// <summary>
    /// The stored value is the preference, not the painted mode: <c>system</c> is resolved by the
    /// browser on each paint. An unknown or absent value reads as dark, the application's default
    /// before the System option existed.
    /// </summary>
    internal static OmniAppearance ParseAppearance(string? stored) => stored switch
    {
        "light" => OmniAppearance.Light,
        "system" => OmniAppearance.System,
        _ => OmniAppearance.Dark
    };

    internal static string StoredAppearance(OmniAppearance appearance) => appearance switch
    {
        OmniAppearance.Light => "light",
        OmniAppearance.System => "system",
        _ => "dark"
    };
}
