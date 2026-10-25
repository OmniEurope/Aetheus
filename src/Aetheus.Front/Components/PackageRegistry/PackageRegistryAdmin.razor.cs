// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Aetheus.Front.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Components.PackageRegistry;

public partial class PackageRegistryAdmin : Aetheus.Front.Components.Shared.RealtimeAdminGridPageBase<PackageRegistryPackageDto>
{
    private List<PackageRegistryPackageDto> _packages = [];
    private Func<string, string>? _kindText;
    private Func<string, string> KindText => _kindText ??= GridFilterText.ForEnum<PackageRegistryKind>(L);

    // Tab order: all, one per type, then the source addresses.
    private static readonly IReadOnlyList<string> TabSlugs = ["all", "nuget", "npm", "maven", "addresses"];
    private const int AddressesTab = 4;
    private int _tab;
    private PackageRegistryKind? _kind;
    private bool ShowsAddresses => _tab == AddressesTab;

    private async Task OnTabChangedAsync(int tab)
    {
        _tab = tab;
        _kind = tab switch
        {
            1 => PackageRegistryKind.NuGet,
            2 => PackageRegistryKind.Npm,
            3 => PackageRegistryKind.Maven,
            _ => null
        };
        // The grid is unmounted on the addresses tab and loads itself when it comes back.
        if (!ShowsAddresses)
            await ResetSearchAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        if (!await InitializeAdminAsync(AdminEntities.PackageRegistry, "PackageRegistry"))
            return;
    }

    /// <summary>One page of the export, with the search, sort and column filters of the
    /// grid.</summary>
    private Task<PaginatedResult<PackageRegistryPackageDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(PackageRegistryPackageDto.Name));
        return Api.Packages.GetRegistryPackagesAsync(page, pageSize, _search, kind: _kind,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        if (!Auth.IsAdmin) return;
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(PackageRegistryPackageDto.Name));
        _loading = true;
        try
        {
            // The header filters are applied by the API, not by the grid.
            var result = await Api.Packages.GetRegistryPackagesAsync(page, pageSize, _search, kind: _kind,
                sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
            _packages = result.Items;
            _totalCount = result.TotalCount;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OpenPackageAsync(PackageRegistryPackageDto package)
    {
        await Dialog.OpenAsync<PackageRegistryPackageDialog>(package.Name,
            new Dictionary<string, object?> { ["PackageId"] = package.Id },
            new OmniDialogOptions { Width = "820px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
        await ReloadAsync();
    }

}
