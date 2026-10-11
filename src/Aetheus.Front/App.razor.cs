// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;

namespace Aetheus.Front;

/// <summary>
/// The application root: OE's theme scope bound to the stored look, around the overlay host and the router.
/// </summary>
public partial class App : IDisposable
{
    internal const string ThemeScopeId = "app-theme-scope";

    /// <summary>Recette R2-112: the localStorage key of the snapshot OmniThemeScope writes and omni-boot.js
    /// replays before the first frame (data-theme-snapshot-key in index.html).</summary>
    internal const string ThemeSnapshotKey = "aetheus:theme";

    private static readonly OmniNotificationOptions NotificationOptions = new(ShowCountdown: true, Group: true);

    [Inject] private OmniOverlayService Overlays { get; set; } = default!;

    [Inject] private SiteAppearanceState Appearance { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        Appearance.Changed += OnAppearanceChanged;
        await Appearance.LoadAsync();
    }

    private void OnAppearanceChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Appearance.Changed -= OnAppearanceChanged;
}
