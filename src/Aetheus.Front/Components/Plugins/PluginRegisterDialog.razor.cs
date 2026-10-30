// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Plugins;

// X4D8: the plugin-register form, extracted from PluginManagement's inline panel into a dialog so the
// management page is a clean full-height grid (consistent with Alerts/AlertEditDialog). Closes with
// `true` on a successful registration so the caller reloads the list; dismiss leaves it untouched.
public partial class PluginRegisterDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private readonly RegisterPluginRequest _model = new();
    private bool _busy;

    private List<OmniOption<PluginType>> TypeOptions => Enum.GetValues<PluginType>()
        .Select(t => new OmniOption<PluginType>(t, L.Localize(t)))
        .ToList();

    private async Task SubmitAsync()
    {
        _busy = true;
        try
        {
            var result = await Api.Settings.RegisterPluginAsync(_model);
            if (result is not null)
            {
                Toast.Success("Registered", "PluginRegistered");
                Dialog.Close(true);
            }
            else
            {
                Toast.Error("Error", "SaveFailed");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Cancel() => Dialog.Close(false);

    public static OmniDialogOptions OmniDialogOptions() =>
        new() { Width = "640px", CloseDialogOnOverlayClick = false };
}
