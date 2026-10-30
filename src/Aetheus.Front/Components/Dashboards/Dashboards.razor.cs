// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Dashboards;

public partial class Dashboards
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;

    private List<DashboardDto> _dashboards = [];
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        if (!Auth.IsAdmin)
        {
            Navigation.NavigateTo("/");
            return;
        }

        Breadcrumb.Set(new BreadcrumbItem(L["Administration"], "/admin"), new BreadcrumbItem(L["Dashboards"]));
        try { _dashboards = await Api.Monitoring.GetDashboardsAsync(); }
        catch (HttpRequestException) { _dashboards = []; }
        _loading = false;
    }

    private async Task CreateDashboard()
    {
        // F-45: prompt the user for a name via an application dialog instead of the native browser prompt().
        var defaultName = $"{L["Dashboard"]} {_dashboards.Count + 1}";
        var dialogResult = await Dialog.OpenAsync<DashboardNameDialog>(
            L["NewDashboardNamePrompt"].Value,
            new Dictionary<string, object?> { { "InitialName", defaultName } },
            new OmniDialogOptions { Width = "400px", AutoFocusFirstElement = false });

        if (dialogResult is not string name || string.IsNullOrWhiteSpace(name)) return;

        var result = await Api.Monitoring.CreateDashboardAsync(new CreateDashboardRequest
        {
            Name = name.Trim(),
            Widgets = []
        });
        if (result is not null)
        {
            _dashboards = await Api.Monitoring.GetDashboardsAsync();
            Toast.Success("Created", "DashboardCreated");
        }
    }

    private void EditDashboard(DashboardDto dashboard)
    {
        Navigation.NavigateTo($"/dashboards/{dashboard.Id}");
    }

    private async Task DeleteDashboard(int id)
    {
        var confirmed = await Dialog.Confirm(L["DeleteConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var success = await Api.Monitoring.DeleteDashboardAsync(id);
            if (success)
            {
                _dashboards = await Api.Monitoring.GetDashboardsAsync();
                Toast.Success("Deleted", "Deleted");
            }
            else
            {
                Toast.Error("Error", "DeleteFailed");
            }
        });
    }
}
