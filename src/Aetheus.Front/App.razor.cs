// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;

namespace Aetheus.Front;

/// <summary>
/// The application root: OE's theme scope bound to the stored look, around the overlay host and the router.
/// </summary>
public partial class App : IDisposable
{
    /// <summary>The scope's id, by which <c>layout.js</c> keeps the boot copy of its tokens.</summary>
    internal const string ThemeScopeId = "app-theme-scope";

    private static readonly OmniNotificationOptions NotificationOptions = new(ShowCountdown: true, Group: true);

    [Inject] private OmniOverlayService Overlays { get; set; } = default!;

    [Inject] private SiteAppearanceState Appearance { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        Appearance.Changed += OnAppearanceChanged;
        await Appearance.LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The boot script of index.html paints the scope's last tokens before the first frame; from now on
        // layout.js copies whatever OmniThemeScope paints, so the next boot starts in the same colours.
        if (firstRender)
            await JS.InvokeVoidAsync("Aetheus.followThemeScope", ThemeScopeId);
    }

    private void OnAppearanceChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Appearance.Changed -= OnAppearanceChanged;
}
