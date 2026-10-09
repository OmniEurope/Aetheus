// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Settings;

/// <summary>
/// Recette R-232 / R-390: the theme, palette, font and scales of the whole site. Themes, palettes and
/// fonts are those of the OmniEurope.Blazor catalogue; what the site stores is their names, and the
/// application's <c>OmniThemeScope</c> resolves and paints their tokens (STD-SHELL). No theme is OE's first
/// one, Essentiel, which the shipped stylesheet already draws (user decision 2026-09-29: no separate
/// "Aetheus" theme).
/// </summary>
internal static class SiteAppearance
{
    /// <summary>The lowest and highest level of the text size, density and control size scales; 5 is as drawn.</summary>
    public const int MinimumLevel = 1;
    public const int MaximumLevel = 10;
    public const int DefaultLevel = 5;

    /// <summary>The catalogue theme of that name, or null for the default one (Essentiel, as shipped).</summary>
    public static OmniThemePreset? Preset(string? name) => string.IsNullOrWhiteSpace(name)
        ? null
        : OmniThemePresets.All.FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.Ordinal));

    /// <summary>The catalogue palette of that name, or null for the theme's own.</summary>
    public static OmniThemePalette? Palette(string? name) => string.IsNullOrWhiteSpace(name)
        ? null
        : OmniThemePalettes.All.FirstOrDefault(palette => string.Equals(palette.Name, name, StringComparison.Ordinal));

    /// <summary>The catalogue font of that name, or null for the one the theme is drawn with.</summary>
    public static OmniThemeFont? Font(string? name) => string.IsNullOrWhiteSpace(name)
        ? null
        : OmniThemeFonts.All.FirstOrDefault(font => string.Equals(font.Name, name, StringComparison.Ordinal));

    /// <summary>The stored density; anything unknown reads as the shipped one.</summary>
    public static OmniDensity ParseDensity(string? stored) => stored switch
    {
        "compact" => OmniDensity.Compact,
        "spacious" => OmniDensity.Spacious,
        _ => OmniDensity.Comfortable
    };

    public static string StoredDensity(OmniDensity density) => density switch
    {
        OmniDensity.Compact => "compact",
        OmniDensity.Spacious => "spacious",
        _ => "comfortable"
    };

    /// <summary>A stored text size or control size level; anything missing or out of range is as drawn.</summary>
    public static int ParseLevel(string? stored) =>
        int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var level)
        && level is >= MinimumLevel and <= MaximumLevel ? level : DefaultLevel;

    public static string StoredLevel(int level) =>
        Math.Clamp(level, MinimumLevel, MaximumLevel).ToString(CultureInfo.InvariantCulture);
}
