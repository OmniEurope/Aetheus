// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerPatchSection
{
    [Parameter] public int ServerId { get; set; }

    /// <summary>From the server DTO - whether the agent can APPLY updates. Null = unknown (never reported).</summary>
    [Parameter] public bool? PatchManagementAvailable { get; set; }

    [Parameter] public EventCallback OnRefreshRequested { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private ServerSecurityUpdatesDto? _status;
    private bool _loading = true;
    private bool _busy;

    protected override async Task OnParametersSetAsync() => await LoadAsync();

    private OmniDataGrid<PendingUpdateDto>? _grid;

    // The security column filters on yes or no.
    private Func<string, string>? _yesNoText;
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);

    /// <summary> Reads the pending updates. <paramref name="live"/> is the task-completion path:
    /// the grid stays on screen (no loader) and is told before the new list arrives, so the updates
    /// it did not hold read bold. </summary>
    private async Task LoadAsync(bool live = false)
    {
        if (!live) _loading = true;
        try
        {
            var status = await Api.Security.GetSecurityUpdatesAsync(ServerId);
            if (live && _grid is not null) await _grid.RefreshAsync();
            _status = status;
        }
        catch (HttpRequestException)
        {
            _status = null; // surfaces as "unknown"
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Called by the hosting page when a task completes so a finished upgrade refreshes the counts.</summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification _)
    {
        await LoadAsync(live: true);
        StateHasChanged();
    }

    private async Task CheckAsync() => await RunUpgradeAsync(dryRun: true, "PatchDryRunQueued");

    private async Task ApplyAsync()
    {
        var confirmed = await Dialog.Confirm(L["PatchApplyConfirm"].Value, L["PatchApply"].Value,
            new OmniConfirmOptions { OkButtonText = L["PatchApply"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        await RunUpgradeAsync(dryRun: false, "PatchApplyQueued");
    }

    private async Task RunUpgradeAsync(bool dryRun, string successKey)
    {
        _busy = true;
        try
        {
            var taskId = await Api.Security.UpgradeSystemAsync(ServerId, dryRun);
            if (taskId is not null)
                Toast.Success(successKey, successKey);
            else
                Toast.Error("PatchActionFailed", "PatchActionFailed");
        }
        catch (HttpRequestException)
        {
            Toast.Error("PatchActionFailed", "PatchActionFailed");
        }
        finally
        {
            _busy = false;
        }
    }

    // Only the agent with the apt patch-manage grant can apply; unknown (null) is treated as not-yet-capable.
    private bool CanApply => PatchManagementAvailable == true;

    private OmniTone SecurityBadgeStyle => (_status?.PendingSecurity ?? 0) > 0 ? OmniTone.Danger : OmniTone.Neutral;
}
