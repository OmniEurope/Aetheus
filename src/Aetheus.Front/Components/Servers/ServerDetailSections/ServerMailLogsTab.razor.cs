// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Journal of postfix, dovecot, opendkim or rspamd read as root by the mail helper,
/// optionally filtered by a queue id or an address, displayed once the task completes.</summary>
public partial class ServerMailLogsTab
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    internal static IReadOnlyList<string> Units => MailValidation.ManagedUnits;

    private string _unit = "postfix";
    private int _lines = 100;
    private string _filter = string.Empty;
    private int? _pendingTaskId;
    private string? _content;

    internal string? Content => _content;

    private async Task FetchAsync()
    {
        var filter = string.IsNullOrWhiteSpace(_filter) ? null : _filter.Trim();
        if (filter is not null && !MailValidation.IsValidLogFilter(filter))
        {
            Toast.Warning("MailLogFilter", "ActionFailed");
            return;
        }
        var queued = await Api.Mail.FetchMailLogsAsync(ServerId, new MailLogRequest { LogType = _unit, Lines = _lines, Filter = filter });
        if (queued is null)
        {
            Toast.Error(L["ActionFailed"]);
            return;
        }
        _pendingTaskId = queued.TaskId;
        Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    /// <summary>Called by the mail section for every completed task of the server.</summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification notification)
    {
        if (notification.TaskId != _pendingTaskId) return;
        _pendingTaskId = null;
        _content = await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId);
        await InvokeAsync(StateHasChanged);
    }
}
