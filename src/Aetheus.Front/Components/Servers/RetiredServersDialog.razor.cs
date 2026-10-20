// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

/// <summary>Lists retired servers whose links remain available for re-enrollment and offers
/// permanent deletion.</summary>
public partial class RetiredServersDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;

    private AetheusDataGrid<RetiredServerDto>? _grid;
    private List<RetiredServerDto> _servers = [];
    private readonly HashSet<int> _purgingIds = [];
    private int _totalCount;
    private bool _loading;
    private bool _loadFailed;

    /// <summary>One page of the export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<RetiredServerDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = args.ToSortRequest(nameof(RetiredServerDto.RetiredAt), fallbackDescending: true);
        return Api.Servers.GetRetiredServersAsync(page, pageSize, sortBy, descending, filters: args.ToApiFilters());
    }

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, descending) = args.ToSortRequest(nameof(RetiredServerDto.RetiredAt), fallbackDescending: true);
        // The header filters, applied by the API.
        var filters = args.ToApiFilters();
        await PaginatedGridLoader.LoadAsync(
            () => Api.Servers.GetRetiredServersAsync(page, pageSize, sortBy, descending, filters: filters),
            ApplyResult,
            loading => _loading = loading,
            failed => _loadFailed = failed);
    }

    internal void ApplyResult(PaginatedResult<RetiredServerDto> result) =>
        (_servers, _totalCount) = (result.Items, result.TotalCount);

    private Task RetryAsync() => PaginatedGridLoader.RetryAsync(_grid);

    private async Task OnPurgeServer(RetiredServerDto server)
    {
        if (_purgingIds.Contains(server.Id)) return;
        var confirmed = await Dialog.Confirm(
            string.Format(L["PurgeServerConfirm"], server.Name),
            L["PurgeServer"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Purge"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;

        await PurgeServerConfirmedAsync(server);
    }

    internal async Task PurgeServerConfirmedAsync(RetiredServerDto server)
    {
        if (!_purgingIds.Add(server.Id)) return;
        StateHasChanged();
        var purged = false;
        try
        {
            var status = await Api.Servers.PurgeServerAsync(server.Id);
            purged = status.Success;
            if (purged)
                Toast.Success("ServerPurged", "ServerPurgedDetail", server.Name);
            else
                Toast.Error("Error", "ServerPurgeFailed");
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "ServerPurgeFailed");
        }
        catch (TaskCanceledException)
        {
            Toast.Error("Error", "ServerPurgeFailed");
        }
        finally
        {
            _purgingIds.Remove(server.Id);
            if (purged)
            {
                _servers = _servers.Where(item => item.Id != server.Id).ToList();
                _totalCount = Math.Max(0, _totalCount - 1);
            }
            StateHasChanged();
            if (_grid is not null) await _grid.Reload();
        }
    }

    private void Close() => Dialog.Close();
}
