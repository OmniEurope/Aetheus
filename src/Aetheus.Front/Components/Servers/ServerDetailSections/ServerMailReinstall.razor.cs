// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// "Réinstaller la messagerie" (recette R2-083 / R2-088, decision of 2026-10-03: erase everything): asks for
/// the domain typed back, queues <c>mail-manage purge</c> and shows its outcome on the page, the last log
/// line of a failure included. One click queues one purge: the button is busy from the click until the
/// task completes (R2-085).
/// </summary>
public partial class ServerMailReinstall
{
    /// <summary>The first mail-manage helper version that has the <c>purge</c> sub-command.</summary>
    internal const int MinimumHelperVersion = 3;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ILogger<ServerMailReinstall> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public MailDataDto Mail { get; set; } = new();
    [Parameter] public bool CanManage { get; set; }

    private bool _starting;
    private int? _taskId;
    private ReinstallOutcome? _outcome;

    /// <summary>A stack is on the host: configured, or provisioned and left half-way.</summary>
    internal bool StackDetected => Mail.IsInstalled || Mail.IsManagedByAetheus;

    internal bool Running => _starting || _taskId is not null;

    internal ReinstallOutcome? Outcome => _outcome;

    private bool HelperReady => Mail.HelperVersion >= MinimumHelperVersion;

    private bool CanPurge => CanManage && HelperReady;

    private string? DisabledReason =>
        !CanManage ? L["MailSetupCapabilityRequired"].Value
        : !HelperReady ? string.Format(L["MailReinstallHelperRequired"], Mail.HelperVersion, MinimumHelperVersion)
        : null;

    internal sealed record ReinstallOutcome(bool Succeeded, TaskExecutionStatus Status, string LastLine);

    private int? _shownServerId;

    // The section is reused when the user moves to another server: its purge is not this one's.
    protected override void OnParametersSet()
    {
        if (_shownServerId == ServerId) return;
        _shownServerId = ServerId;
        _taskId = null;
        _outcome = null;
    }

    private async Task PurgeAsync()
    {
        if (Running || !CanPurge) return;
        _starting = true;
        try
        {
            var word = await ConfirmationWordAsync();
            if (word is null) return;
            var typed = await Dialog.OpenAsync<MailReinstallDialog>(L["MailReinstall"],
                new Dictionary<string, object?> { { nameof(MailReinstallDialog.Word), word } },
                new OmniDialogOptions { Width = "36rem", AutoFocusFirstElement = false });
            if (typed is not string confirmation) return;

            var queued = await Api.Mail.PurgeMailStackAsync(ServerId, new MailPurgeRequest { Confirmation = confirmation });
            if (queued is null) return; // the refusal is already shown with its reason by the error toast
            _outcome = null;
            _taskId = queued.TaskId;
            Toast.Info("TaskQueued", "MailWaitingForTask");
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail reinstall failed for server {ServerId}", ServerId);
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _starting = false;
        }
    }

    // The domain the backend accepts (its mail rows), else the mail host name.
    private async Task<string?> ConfirmationWordAsync()
    {
        var domains = await Api.Mail.GetMailDomainsAsync(ServerId);
        var word = domains.Select(d => d.Name).Append(Mail.Hostname).FirstOrDefault(MailValidation.IsValidDomainName);
        if (word is null) Toast.Error(L["MailReinstallNoName"]);
        return word;
    }

    /// <summary>Called by the mail section for every completed task of the server.</summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.TaskId != _taskId) return;
        _taskId = null;
        var succeeded = notification.Status == TaskExecutionStatus.Success;
        var lastLine = succeeded ? string.Empty : await LastLogLineAsync(notification.TaskId);
        _outcome = new ReinstallOutcome(succeeded, notification.Status, lastLine);
        await InvokeAsync(StateHasChanged);
    }

    private async Task<string> LastLogLineAsync(int taskId)
    {
        try
        {
            var output = await MailTaskOutputLoader.ReadAsync(Api, taskId);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? L["MailReinstallNoLog"].Value;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail reinstall log read failed for task {TaskId}", taskId);
            return L["MailReinstallNoLog"].Value;
        }
    }
}
