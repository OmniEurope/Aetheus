// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Components.PackageRegistry;

public partial class PackageRegistryPackageDialog : ComponentBase, IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    [Parameter] public int PackageId { get; set; }

    private PackageRegistryPackageDetailDto? _package;
    private AetheusDataGrid<PackageRegistryVersionDto>? _grid;
    private static readonly TimeSpan NewRowHighlight = TimeSpan.FromSeconds(5);
    // Header filter text, built once so the column sees the same delegate on every render.
    private Func<string, string>? _listedText;
    private Func<string, string> ListedText => _listedText ??= value =>
        bool.TryParse(value, out var listed) ? L[listed ? "PackageVersionListed" : "PackageVersionUnlisted"].Value : value;
    private bool _loading = true;
    private AdminEntitySubscription? _adminRt;
    private readonly TrailingReloadCoalescer _realtimeReload = new(500);

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _adminRt = new AdminEntitySubscription(HubFactory);
        await _adminRt.StartAsync(
            AdminEntities.PackageRegistry,
            () => InvokeAsync(() => _realtimeReload.RequestAsync(RefreshFromPushAsync)));
    }

    /// <summary> A live change re-reads the package without the loader; the grid notes the rows it
    /// holds before the new list lands, so a version published meanwhile reads bold for a few
    /// seconds. </summary>
    private async Task RefreshFromPushAsync()
    {
        try
        {
            var package = await Api.Packages.GetRegistryPackageAsync(PackageId);
            if (package is null) return;
            if (_grid is not null) await _grid.Refresh();
            _package = package;
        }
        catch (HttpRequestException) { return; }
        StateHasChanged();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _package = await Api.Packages.GetRegistryPackageAsync(PackageId);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ToggleListedAsync(PackageRegistryVersionDto version)
    {
        var status = await Api.Packages.UpdateRegistryVersionAsync(PackageId, version.Id, !version.IsListed);
        if (!status.Success)
        {
            Toast.Error("Error", "SaveFailed");
            return;
        }
        await LoadAsync();
        Toast.Success("Saved", "Saved");
    }

    private void Close() => Dialog.Close();

    public async ValueTask DisposeAsync()
    {
        if (_adminRt is not null) await _adminRt.DisposeAsync();
    }
}
