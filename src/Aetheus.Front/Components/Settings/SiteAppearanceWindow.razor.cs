// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Settings;

/// <summary> The site's appearance in OE's <see cref="OmniAppearanceWindow"/>, opened from the user
/// menu. It reads the user's appearance when it opens; each change is stored and repainted at once by
/// OE's <see cref="OmniAppearanceState"/>, the same source the settings page uses. </summary>
public partial class SiteAppearanceWindow : IDisposable
{
    [Inject] private OmniAppearanceState Appearance { get; set; } = default!;

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private bool _open;

    protected override void OnInitialized() => Appearance.Changed += OnAppearanceChanged;

    /// <summary>Opens the window on the user's appearance.</summary>
    public async Task OpenAsync()
    {
        await Appearance.LoadAsync();
        _open = true;
        StateHasChanged();
    }

    private void OnAppearanceChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Appearance.Changed -= OnAppearanceChanged;
}
