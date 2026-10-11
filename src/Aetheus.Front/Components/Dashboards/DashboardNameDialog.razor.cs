// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Components.Dashboards;

public partial class DashboardNameDialog
{
    [Parameter] public string InitialName { get; set; } = string.Empty;

    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private string _value = string.Empty;

    protected override void OnInitialized() => _value = InitialName;

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_value))
            Dialog.Close(_value.Trim());
        else if (e.Key == "Escape")
            Dialog.Close();
    }

    private void Save() => Dialog.Close(_value.Trim());
}
