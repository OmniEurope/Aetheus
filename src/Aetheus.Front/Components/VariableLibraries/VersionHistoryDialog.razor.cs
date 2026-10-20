// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.VariableLibraries;

public partial class VersionHistoryDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter] public int LibraryId { get; set; }
    [Parameter] public int EntryId { get; set; }

    /// <summary>The entry's current version, the one there is no point restoring.</summary>
    [Parameter] public int CurrentVersion { get; set; }

    private AetheusDataGrid<VariableEntryVersionDto>? _grid;
    private List<VariableEntryVersionDto> _versions = [];
    private int _totalCount;
    private bool _loading;
    private bool _loadFailed;
    private bool _restoring;
    private int _currentVersion;

    /// <summary>The date column shows local time, so its range filter reads the same
    /// instant.</summary>
    private static readonly Func<VariableEntryVersionDto, object?> LocalChangedAt = version => version.ChangedAt.ToLocalTime();

    protected override void OnParametersSet() => _currentVersion = CurrentVersion;

    /// <summary>One page of the versions export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<VariableEntryVersionDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(25);
        var (sortBy, descending) = args.ToSortRequest("Version", true);
        return Api.Variables.GetVariableEntryVersionsPageAsync(
            LibraryId, EntryId, page, pageSize, sortBy: sortBy, sortDescending: descending, filters: args.ToApiFilters());
    }

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        var pageSize = args.Top ?? 25;
        var page = ((args.Skip ?? 0) / pageSize) + 1;
        var (sortBy, descending) = args.ToSortRequest("Version", true);
        await PaginatedGridLoader.LoadAsync(
            () => Api.Variables.GetVariableEntryVersionsPageAsync(
                LibraryId, EntryId, page, pageSize, sortBy: sortBy, sortDescending: descending,
                filters: args.ToApiFilters()),
            ApplyResult,
            loading => _loading = loading,
            failed => _loadFailed = failed);
    }

    private void ApplyResult(PaginatedResult<VariableEntryVersionDto> result) =>
        (_versions, _totalCount) = (result.Items, result.TotalCount);

    /// <summary> Restoring writes the version's key and value back as a new change of the entry,
    /// through the ordinary update, so the history keeps the restore as its own line. </summary>
    private async Task RestoreAsync(VariableEntryVersionDto version)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["RestoreVersionConfirm"].Value, version.Version),
            L["RestoreVersion"].Value,
            new OmniConfirmOptions { Destructive = true, ConfirmIcon = OmniIconName.ArrowUUpLeft, OkButtonText = L["Restore"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        _restoring = true;
        try
        {
            var outcome = await Api.Variables.UpdateVariableEntryAsync(LibraryId, EntryId, new UpdateVariableEntryRequest
            {
                Key = version.Key,
                Value = version.Value
            });
            if (outcome.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // The restored key already belongs to another entry.
                Toast.Error("Error", "LibraryEntryKeyExists", version.Key);
                return;
            }
            if (!outcome.IsSuccess)
            {
                Toast.Error("Error", "OperationFailed");
                return;
            }
            Toast.Success("Saved", "VersionRestored");
            Dialog.Close(true);
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "OperationFailed");
        }
        finally
        {
            _restoring = false;
        }
    }

    private Task RetryAsync() => PaginatedGridLoader.RetryAsync(_grid);

    private void Close() => Dialog.Close();
}
