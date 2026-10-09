// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Aetheus.Front.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Components.PackageFeeds;

public partial class PackageFeedsAdmin : Aetheus.Front.Components.Shared.RealtimeAdminGridPageBase<PackageFeedDto>
{
    private List<PackageFeedDto> _feeds = [];
    private Func<string, string>? _feedTypeText;
    private Func<string, string> FeedTypeText => _feedTypeText ??= GridFilterText.ForEnum<PackageFeedType>(L);

    protected override async Task OnInitializedAsync()
    {
        if (!await InitializeAdminAsync(AdminEntities.PackageFeed, "PackageFeeds"))
            return;
    }

    /// <summary>Recette R2-124: one page of the export, with the search, sort and column filters of the grid.</summary>
    private Task<PaginatedResult<PackageFeedDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        return Api.Packages.GetPackageFeedsAsync(page, pageSize, _search, sortBy, sortDescending, filters: args.ToApiFilters());
    }

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest("Name");
        _loading = true;
        try
        {
            // Recette R-210: the header filters are applied by the API, not by the grid.
            var result = await Api.Packages.GetPackageFeedsAsync(
                page, pageSize, _search, sortBy, sortDescending, filters: args.ToApiFilters());
            _feeds = result.Items;
            _totalCount = result.TotalCount;
        }
        finally { _loading = false; }
    }

    private async Task OpenCreateAsync()
    {
        var result = await Dialog.OpenAsync<PackageFeedEditDialog>(L["Create"],
            [], new OmniDialogOptions { Width = "620px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        if (result is true) await ReloadAsync();
    }

    private async Task ManagePackagesAsync(PackageFeedDto feed)
    {
        await Dialog.OpenAsync<PackageFeedPackagesDialog>(feed.Name,
            new Dictionary<string, object?> { ["FeedId"] = feed.Id },
            new OmniDialogOptions { Width = "760px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        await ReloadAsync(); // package count may have changed
    }

    private async Task DeleteAsync(PackageFeedDto feed)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["DeletePackageFeedConfirm"], feed.Name), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Ui.RunAsync(
            () => Api.Packages.DeletePackageFeedAsync(feed.Id),
            "Deleted",
            ReloadAsync,
            errorKey: "DeleteFailed",
            successTitleKey: "Deleted");
    }

}
