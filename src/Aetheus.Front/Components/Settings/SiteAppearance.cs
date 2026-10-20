// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Settings;

/// <summary>Reads the appearance values Aetheus stored before OE kept them (R1-66 migration to
/// OmniAppearanceState): catalogue names and scales. An unset theme uses the OE default, Essentiel.</summary>
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

    /// <summary>
    /// The former stored mode: <c>system</c> is resolved by the browser on each paint. An unknown or
    /// absent value reads as dark, the application's default.
    /// </summary>
    public static OmniAppearance ParseAppearance(string? stored) => stored switch
    {
        "light" => OmniAppearance.Light,
        "system" => OmniAppearance.System,
        _ => OmniAppearance.Dark
    };

    /// <summary>The stored density; anything unknown reads as the shipped one.</summary>
    public static OmniDensity ParseDensity(string? stored) => stored switch
    {
        "compact" => OmniDensity.Compact,
        "spacious" => OmniDensity.Spacious,
        _ => OmniDensity.Comfortable
    };

    /// <summary>A stored text size or control size level; anything missing or out of range is as drawn.</summary>
    public static int ParseLevel(string? stored) =>
        int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var level)
        && level is >= MinimumLevel and <= MaximumLevel ? level : DefaultLevel;
}
