// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Components.PackageFeeds;

public partial class PackageFeedPackagesDialog : ComponentBase, IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    [Parameter] public int FeedId { get; set; }

    private bool _loading = true;
    private bool _adding;
    private bool _syncing;
    private bool _removing;
    private string? _newPackage;
    private string _feedName = string.Empty;
    private List<PackageEntryDto> _packages = [];
    private AetheusDataGrid<PackageEntryDto>? _grid;
    private static readonly TimeSpan NewRowHighlight = TimeSpan.FromSeconds(5);
    private PackageFeedSyncResultDto? _lastSync;
    private AdminEntitySubscription? _adminRt;
    private readonly TrailingReloadCoalescer _realtimeReload = new(500);

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _adminRt = new AdminEntitySubscription(HubFactory);
        await _adminRt.StartAsync(
            AdminEntities.PackageFeed,
            () => InvokeAsync(() => _realtimeReload.RequestAsync(RefreshFromPushAsync)));
    }

    /// <summary> A live change re-reads the feed without the loader; the grid notes the rows it
    /// holds before the new list lands, so a package added elsewhere reads bold for a few seconds.
    /// </summary>
    private async Task RefreshFromPushAsync()
    {
        try
        {
            var detail = await Api.Packages.GetPackageFeedAsync(FeedId);
            if (detail is null) return;
            if (_grid is not null) await _grid.Refresh();
            _feedName = detail.Name;
            _packages = detail.Packages;
        }
        catch (HttpRequestException) { return; }
        StateHasChanged();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var detail = await Api.Packages.GetPackageFeedAsync(FeedId);
            _feedName = detail?.Name ?? FeedId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _packages = detail?.Packages ?? [];
        }
        catch (HttpRequestException) { _packages = []; }
        finally { _loading = false; }
    }

    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(_newPackage)) return;
        _adding = true;
        try
        {
            var result = await Api.Packages.AddPackageToFeedAsync(FeedId, new AddPackageRequest { Name = _newPackage.Trim() });
            if (result is not null)
            {
                _newPackage = null;
                await LoadAsync();
                Toast.Success("Added", "Saved");
            }
            else Toast.Error("Error", "SaveFailed");
        }
        catch (HttpRequestException) { Toast.Error("Error", "SaveFailed"); }
        finally { _adding = false; }
    }

    private async Task SyncAsync()
    {
        _syncing = true;
        StateHasChanged();
        try
        {
            _lastSync = await Api.Packages.SyncPackageFeedAsync(FeedId);
            if (_lastSync is null) Toast.Error("Error", "SaveFailed");
            else
            {
                await LoadAsync();
                Toast.Success("Saved", "Saved");
            }
        }
        catch (HttpRequestException) { Toast.Error("Error", "SaveFailed"); }
        finally { _syncing = false; StateHasChanged(); }
    }

    private async Task RemoveAsync(PackageEntryDto package)
    {
        var confirmed = await Dialog.Confirm(
            RemoveConfirmationMessage(package),
            L["Remove"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Remove"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;

        // R-99: the dialog is veiled before the removal runs (the confirmation returned without a render).
        _removing = true;
        StateHasChanged();
        try
        {
            var status = await Api.Packages.RemovePackageFromFeedAsync(FeedId, package.Id);
            if (status.Success)
            {
                _packages.RemoveAll(p => p.Id == package.Id);
                Toast.Success("Deleted", "Deleted");
            }
            else Toast.Error("Error", "DeleteFailed");
        }
        finally
        {
            _removing = false;
            StateHasChanged();
        }
    }

    private string RemoveConfirmationMessage(PackageEntryDto package)
    {
        var message = L["RemovePackageFromFeedConfirm", package.Name, _feedName];
        return message.Value == message.Name
            ? $"{message.Name}: {package.Name} / {_feedName}"
            : message.Value;
    }

    private void Close() => Dialog.Close();

    public async ValueTask DisposeAsync()
    {
        if (_adminRt is not null) await _adminRt.DisposeAsync();
    }
}
