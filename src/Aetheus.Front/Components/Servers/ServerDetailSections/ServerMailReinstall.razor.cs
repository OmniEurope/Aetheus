// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Mail reinstallation requires typed domain confirmation before queuing <c>mail-manage
/// purge</c>. The action stays busy through task completion and exposes failure output.</summary>
public partial class ServerMailReinstall : IDisposable
{
    /// <summary>The first mail-manage helper version that has the <c>purge</c> sub-command.</summary>
    internal const int MinimumHelperVersion = 3;

    /// <summary>The outlet beside the header's Réinstaller button where the status list puts its
    /// "..." menu of mail actions.</summary>
    internal const string HeaderMenuSection = "mail-header-menu";

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ILogger<ServerMailReinstall> Logger { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public MailDataDto Mail { get; set; } = new();
    [Parameter] public bool CanManage { get; set; }

    /// <summary>The server whose agent runs the purge (SITE-OFFLINE-001: nothing is queued while it is offline).</summary>
    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;

    /// <summary>Raised when a purge starts or ends, so the mail section hides its service actions
    /// and state list meanwhile.</summary>
    [Parameter] public EventCallback RunningChanged { get; set; }

    [Inject] private TimeProvider Clock { get; set; } = default!;

    private bool _starting;
    private int? _taskId;
    private DateTimeOffset? _queuedAt;
    private PeriodicTimer? _ticker;

    /// <summary>A purge was queued and has not completed: the section shows only the banner.</summary>
    internal bool Erasing => _taskId is not null;

    private string Elapsed => _queuedAt is { } queued
        ? (Clock.GetUtcNow() - queued).ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

    private ReinstallOutcome? _outcome;

    /// <summary>A stack is on the host: configured, or provisioned and left half-way.</summary>
    internal bool StackDetected => Mail.IsInstalled || Mail.IsManagedByAetheus;

    internal bool Running => _starting || _taskId is not null;

    internal ReinstallOutcome? Outcome => _outcome;

    private bool HelperReady => Mail.HelperVersion >= MinimumHelperVersion;

    private bool AgentOffline => AgentOfflineGuard.IsOffline(Server);

    private bool CanPurge => CanManage && HelperReady && !AgentOffline;

    private string? DisabledReason =>
        !CanManage ? L["MailSetupCapabilityRequired"].Value
        : AgentOffline ? L["AgentOfflineActionBlocked"].Value
        : !HelperReady ? string.Format(L["MailReinstallHelperRequired"], Mail.HelperVersion, MinimumHelperVersion)
        : null;

    internal sealed record ReinstallOutcome(bool Succeeded, TaskExecutionStatus Status, string LastLine);

    private int? _shownServerId;

    // The section is reused when the user moves to another server: its purge is not this one's.
    protected override void OnParametersSet()
    {
        if (_shownServerId == ServerId) return;
        _shownServerId = ServerId;
        StopTicker();
        _taskId = null;
        _outcome = null;
    }

    private async Task PurgeAsync()
    {
        if (Running || AgentOfflineGuard.Blocks(Server, Toast) || !CanPurge) return;
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
            _queuedAt = Clock.GetUtcNow();
            StartTicker();
            Toast.Info("TaskQueued", "MailWaitingForTask");
            await RunningChanged.InvokeAsync();
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
        StopTicker();
        var succeeded = notification.Status == TaskExecutionStatus.Success;
        var lastLine = succeeded ? string.Empty : await LastLogLineAsync(notification.TaskId);
        _outcome = new ReinstallOutcome(succeeded, notification.Status, lastLine);
        await InvokeAsync(StateHasChanged);
        await RunningChanged.InvokeAsync();
    }

    // The elapsed time moves once a second while the purge runs; nothing ticks otherwise.
    private void StartTicker()
    {
        StopTicker();
        var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1), Clock);
        _ticker = ticker;
        _ = TickAsync(ticker);
    }

    private async Task TickAsync(PeriodicTimer ticker)
    {
        try
        {
            while (await ticker.WaitForNextTickAsync().ConfigureAwait(false))
                await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // The section went away while the purge ran.
        }
    }

    private void StopTicker()
    {
        _ticker?.Dispose();
        _ticker = null;
        _queuedAt = null;
    }

    public void Dispose() => StopTicker();

    private async Task<string> LastLogLineAsync(int taskId)
    {
        try
        {
            return await ServerTaskOutputLoader.LastLineAsync(Api, taskId) ?? L["MailReinstallNoLog"].Value;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail reinstall log read failed for task {TaskId}", taskId);
            return L["MailReinstallNoLog"].Value;
        }
    }
}
