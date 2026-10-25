// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Monitoring;

/// <summary> The purged tables with their size, a confirmed « Récupérer la place » per table, and
/// the results measured before and after. The page reads the status again after a start; the server
/// refuses a start while a pipeline runs or another table is being processed. </summary>
public partial class ReclaimSpaceView
{

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private ReclaimSpaceStatusDto? _status;
    private bool _loading = true;
    private bool _loadFailed;

    private bool CanStart => _status is { ActivePipelineRuns: 0, RunningTable: null };

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    private async Task LoadAsync()
    {
        try
        {
            _status = await Api.Monitoring.GetReclaimSpaceStatusAsync();
            _loadFailed = _status is null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _loadFailed = true;
        }
    }

    private async Task ReclaimAsync(string table)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(CultureInfo.CurrentCulture, L["ReclaimSpaceConfirm"], table),
            L["ReclaimSpace"],
            new OmniConfirmOptions { OkButtonText = L["ReclaimSpaceAction"], ConfirmIcon = OmniIconName.Broom });
        if (confirmed != true) return;
        if (await Api.Monitoring.StartReclaimSpaceAsync(table))
            Toast.Success("ReclaimSpace", "ReclaimSpaceStarted", table);
        else
            Toast.Error("ReclaimSpace", "ReclaimSpaceRefused");
        await LoadAsync();
    }

    internal static string Megabytes(long? bytes) => bytes is { } value ? DisplayFormatting.Size(value) : "-";

    private string ResultText(ReclaimSpaceResultDto result) =>
        result.CompletedAt is null ? L["ReclaimSpaceInProgress"] : result.Succeeded ? L["ReclaimSpaceDone"] : L["ReclaimSpaceFailedShort"];

    private static OmniTone ResultTone(ReclaimSpaceResultDto result) =>
        result.CompletedAt is null ? OmniTone.Info : result.Succeeded ? OmniTone.Success : OmniTone.Danger;
}
