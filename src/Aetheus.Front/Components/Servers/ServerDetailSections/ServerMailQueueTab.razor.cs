// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Typed view of the Postfix queue (<c>postqueue -j</c> read from the task log),
/// per-message deletion and flush.</summary>
public partial class ServerMailQueueTab
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter] public int QueueSize { get; set; }
    [Parameter] public bool CanManage { get; set; }
    [Parameter] public MailDeliveryStateDto Delivery { get; set; } = new();
    [Parameter] public string HostIpv4 { get; set; } = string.Empty;
    /// <summary>Reported mail-manage helper version; the retry tuning needs <see cref="RetryTuningMinimumHelperVersion"/>.</summary>
    [Parameter] public int HelperVersion { get; set; } = RetryTuningMinimumHelperVersion;

    internal const int RetryTuningMinimumHelperVersion = 6;

    private List<MailQueueItemDto> _items = [];
    private OmniDataGrid<MailQueueItemDto>? _grid;
    private int? _pendingTaskId;
    private bool _loaded;

    internal IReadOnlyList<MailQueueItemDto> Items => _items;

    private async Task LoadAsync()
    {
        var queued = await Api.Mail.RefreshMailQueueAsync(ServerId);
        if (queued is null)
        {
            Toast.Error(L["ActionFailed"]);
            return;
        }
        _pendingTaskId = queued.TaskId;
        Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    private async Task FlushAsync()
    {
        var confirmed = await Dialog.Confirm(L["ConfirmFlushQueue"], L["FlushQueue"],
            new OmniConfirmOptions { OkButtonText = L["Flush"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        var queued = await Api.Mail.QueueMailActionAsync(ServerId, new MailActionRequest { Action = MailAction.FlushQueue });
        if (queued is not null) Toast.Success(L["TaskQueued"]);
        else Toast.Error(L["ActionFailed"]);
    }

    private async Task ApplyRetryTuningAsync()
    {
        var confirmed = await Dialog.Confirm(L["MailConfirmRetryTuning"], L["MailApplyRetryTuning"],
            new OmniConfirmOptions { OkButtonText = L["MailApplyRetryTuning"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        var queued = await Api.Mail.QueueMailActionAsync(ServerId, new MailActionRequest { Action = MailAction.ApplyRetryTuning });
        if (queued is not null) Toast.Success(L["TaskQueued"]);
        else Toast.Error(L["ActionFailed"]);
    }

    private async Task DeleteAsync(string queueId)
    {
        var confirmed = await Dialog.Confirm(string.Format(L["MailConfirmDeleteQueued"], queueId), L["Delete"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        await Veil.RunAsync(async () =>
        {
            var status = await Api.Mail.DeleteQueuedMailAsync(ServerId, queueId);
            if (status.Success)
            {
                _items.RemoveAll(item => item.Id == queueId);
                Toast.Success(L["TaskQueued"]);
            }
            else
            {
                Toast.Error(L["ActionFailed"]);
            }
        });
    }

    /// <summary>Called by the mail section for every completed task of the server.</summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification notification)
    {
        if (notification.TaskId != _pendingTaskId) return;
        _pendingTaskId = null;
        if (notification.Status == TaskExecutionStatus.Success)
        {
            var items = MailTaskOutputParser.ParseQueue(await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId));
            // Recette R-227: told before the new queue arrives, the grid marks the messages it did not hold.
            if (_grid is not null) await _grid.RefreshAsync();
            _items = items;
            _loaded = true;
        }
        else
        {
            Toast.Error(L["ActionFailed"]);
        }
        await InvokeAsync(StateHasChanged);
    }

    internal static string FormatBytes(long bytes) => DisplayFormatting.Size(bytes);

    /// <summary>The export writes the size as the cell does.</summary>
    private static readonly Func<MailQueueItemDto, object?> SizeExport = item => FormatBytes(item.SizeBytes);
}
