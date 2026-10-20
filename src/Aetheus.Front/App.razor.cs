// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front;

/// <summary>
/// The application root: OE's theme scope following the user's appearance, around the overlay host and the router.
/// </summary>
public partial class App
{
    internal const string ThemeScopeId = "app-theme-scope";

    /// <summary>The localStorage key of the snapshot OmniThemeScope writes and omni-boot.js replays
    /// before the first frame (data-theme-snapshot-key in index.html).</summary>
    internal const string ThemeSnapshotKey = "aetheus:theme";

    private static readonly OmniNotificationOptions NotificationOptions = new(ShowCountdown: true, Group: true);

    [Inject] private OmniOverlayService Overlays { get; set; } = default!;
}
