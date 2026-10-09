// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// S-TECH-RB4N: the browser localStorage keys, all derived from <see cref="AppConstants.LocalStoragePrefix"/>
/// so a rebrand changes the prefix in one place instead of chasing string literals across pages.
/// </summary>
internal static class StorageKeys
{
    private const string P = AppConstants.LocalStoragePrefix;

    public const string Theme = P + "theme";
    // Recette R-232: the theme, palette and density of the whole site (layout.js keeps the resolved token
    // values under theme_tokens, for the boot script of index.html).
    public const string ThemePreset = P + "theme_preset";
    public const string ThemePalette = P + "theme_palette";
    public const string Density = P + "density";
    // Recette R-390: the font and the text size and control size scales (1 to 10) of OE's appearance window.
    public const string ThemeFont = P + "theme_font";
    public const string TextSize = P + "text_size";
    public const string ControlSize = P + "control_size";
    // Decision of 2026-10-06: whether a theme that moves its field (Givre, Trou noir) may move it.
    public const string BackdropMotion = P + "backdrop_motion";
    public const string Lang = P + "lang";
    public const string DisplayName = P + "display_name";
    public const string NotifEmail = P + "notif_email";
    public const string NotifPipeline = P + "notif_pipeline";
    public const string NotifServer = P + "notif_server";
    public const string AuthToken = P + "auth_token";
    public const string RefreshToken = P + "refresh_token";
}
