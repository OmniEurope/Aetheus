// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Mail checks follow dependency order and each failure exposes its <see
/// cref="MailStatusFix"/>. Queued tasks are tracked by ID through completion, then their output and
/// refreshed status are read.</summary>
public partial class ServerMailStatusList : IAsyncDisposable
{
    internal const int CurrentHelperVersion = 6;

    private static readonly string[] s_serverKeys = ["services", "hostname-dns", "reverse-dns", "tls"];
    private static readonly string[] s_domainPrefixes = ["mx:", "spf:", "dkim:", "dmarc:"];

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ILogger<ServerMailStatusList> Logger { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public MailDataDto Mail { get; set; } = new();
    [Parameter] public bool CanManage { get; set; }
    [Parameter] public string DefaultSender { get; set; } = string.Empty;

    /// <summary>Raised once measured mailbox usage has been stored, so the accounts grid reloads.</summary>
    [Parameter] public EventCallback OnUsageMeasured { get; set; }

    private MailDiagnosticsDto? _diagnostics;
    private bool _loading;
    private readonly HashSet<MailTaskKind> _queuing = [];
    private readonly Dictionary<int, RunningMailTask> _running = [];
    private string? _failure;
    private string? _failureLog;
    private MailDeliveryResultDto? _delivery;
    private List<MailComponentCheckDto> _checks = [];

    internal MailDeliveryResultDto? Delivery => _delivery;
    internal IReadOnlyList<MailComponentCheckDto> Checks => _checks;

    // No Refresh button; reading the checklist queues no agent task (inventory + DNS), so completed
    // tasks can trigger a reload as safely as heartbeats.
    private ServerLiveFeed? _liveFeed;
    internal ServerLiveFeed? LiveFeed => _liveFeed;

    /// <summary>The lines of the status, in fix order.</summary>
    internal IReadOnlyList<MailDiagnosticItemDto> StatusItems => SelectStatusItems(_diagnostics);

    /// <summary>The status lines of a diagnostics read, in fix order; the page banner (R1-29) reads the same lines.</summary>
    internal static IReadOnlyList<MailDiagnosticItemDto> SelectStatusItems(MailDiagnosticsDto? diagnostics) => diagnostics is null ? [] :
    [
        .. s_serverKeys.Select(key => diagnostics.Items.FirstOrDefault(item => item.Key == key)).OfType<MailDiagnosticItemDto>(),
        .. diagnostics.Items.Where(item => s_domainPrefixes.Any(prefix => item.Key.StartsWith(prefix, StringComparison.Ordinal)))
    ];

    /// <summary>The domain the DNS help names, the first one checked; null without a mail
    /// domain.</summary>
    internal string? HelpDomain => StatusItems.Select(item => item.Key).FirstOrDefault(key => key.StartsWith("mx:", StringComparison.Ordinal))?[3..];

    /// <summary>The first failing status line, with its resolution.</summary>
    internal MailDiagnosticItemDto? FirstRed => StatusItems.FirstOrDefault(item => item.Verdict != MailCheckVerdict.Ok);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await LoadDiagnosticsAsync();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail status load failed for server {ServerId}", ServerId);
            _loading = false;
        }
    }

    protected override void OnParametersSet()
    {
        _liveFeed ??= new ServerLiveFeed(HubFactory);
        if (_liveFeed.ServerId != ServerId)
            _ = _liveFeed.StartAsync(ServerId, ServerLiveFeedTriggers.HeartbeatAndTasks,
                () => InvokeAsync(ReloadFromPushAsync));
    }

    private async Task ReloadFromPushAsync()
    {
        try
        {
            await LoadDiagnosticsAsync();
        }
        catch (HttpRequestException ex)
        {
            // The last status stays on screen; the next push retries.
            Logger.LogWarning(ex, "Mail status reload failed for server {ServerId}", ServerId);
        }
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_liveFeed is not null)
            await _liveFeed.DisposeAsync();
    }

    private async Task LoadDiagnosticsAsync()
    {
        _loading = true;
        try
        {
            _diagnostics = await Api.Mail.GetMailDiagnosticsAsync(ServerId);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>What a line says beside its label: the versions or the certificate when green, the reason when red.</summary>
    internal string LineDetail(MailDiagnosticItemDto item) => (item.Key, item.Verdict) switch
    {
        ("services", MailCheckVerdict.Ok) => string.Join(" · ", new[]
        {
            Mail.PostfixVersion.Length > 0 ? $"Postfix {Mail.PostfixVersion}" : string.Empty,
            Mail.DovecotVersion.Length > 0 ? $"Dovecot {Mail.DovecotVersion}" : string.Empty
        }.Where(part => part.Length > 0)),
        ("tls", MailCheckVerdict.Ok) => CertificateText,
        _ => MailUiText.Detail(L, item.Detail)
    };

    private string CertificateText
    {
        get
        {
            var validity = Mail.Tls.ExpiresAt is { } expires
                ? string.Format(CultureInfo.CurrentCulture, L["MailTlsValidUntil"], expires.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
                : string.Empty;
            return string.IsNullOrEmpty(Mail.Tls.Issuer) ? validity : $"{validity} ({Mail.Tls.Issuer})".Trim();
        }
    }

    // The main part of a split button restarts; each item maps to its own typed action.
    internal static MailAction ServiceAction(string unit, string verb) => (unit, verb) switch
    {
        ("postfix", "start") => MailAction.StartPostfix,
        ("postfix", "stop") => MailAction.StopPostfix,
        ("postfix", "reload") => MailAction.ReloadPostfix,
        ("postfix", _) => MailAction.RestartPostfix,
        ("dovecot", "start") => MailAction.StartDovecot,
        ("dovecot", "stop") => MailAction.StopDovecot,
        ("dovecot", "reload") => MailAction.ReloadDovecot,
        _ => MailAction.RestartDovecot
    };

    /// <summary>The start action of each stopped unit the services line names (OpenDKIM has a restart only).</summary>
    internal static IReadOnlyList<MailAction> StartActions(string stoppedUnits) =>
    [
        .. stoppedUnits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(unit => unit switch
            {
                "postfix" => (MailAction?)MailAction.StartPostfix,
                "dovecot" => MailAction.StartDovecot,
                "opendkim" => MailAction.RestartOpenDkim,
                _ => null
            })
            .OfType<MailAction>()
    ];

    // Track every queued task through completion.

    /// <summary>What a followed task does: which button stays busy, and how its completion is read.</summary>
    internal enum MailTaskKind { Postfix, Dovecot, StartServices, OpenDkim, ObtainCertificate, InstallCertificate, ServerCheck, DeliveryTest, Usage }

    /// <summary>A queued task not completed yet: its kind, its name in the banner, when it was queued.</summary>
    internal sealed record RunningMailTask(MailTaskKind Kind, string Label, DateTimeOffset QueuedAt);

    /// <summary>The followed tasks by id, from the queuing answer until their completion.</summary>
    internal IReadOnlyDictionary<int, RunningMailTask> RunningTasks => _running;

    /// <summary>The outcome of the last failed action task (services, OpenDKIM, certificate), or null.</summary>
    internal string? Failure => _failure;

    /// <summary>The end of the failed task's log, shown under <see cref="Failure"/>.</summary>
    internal string? FailureLog => _failureLog;

    /// <summary>The task kind is being queued or runs: its button is busy.</summary>
    internal bool IsBusy(MailTaskKind kind) => _queuing.Contains(kind) || _running.Values.Any(task => task.Kind == kind);

    private IReadOnlyList<string> RunningLabels => [.. _running.Values.OrderBy(task => task.QueuedAt).Select(task => task.Label).Distinct()];

    private DateTimeOffset RunningSince => _running.Values.Min(task => task.QueuedAt);

    private void Follow(int taskId, MailTaskKind kind, string label)
    {
        _failure = null;
        _failureLog = null;
        _running[taskId] = new RunningMailTask(kind, label, Clock.GetUtcNow());
    }

    /// <summary>The services and actions of the services button's menu, in their listed order.</summary>
    private static readonly string[] MailServiceUnits = ["postfix", "dovecot"];

    private static readonly string[] MailServiceVerbs = ["restart", "start", "stop", "reload"];

    private static OmniIconName ServiceVerbIcon(string verb) => verb switch
    {
        "start" => OmniIconName.Play,
        "stop" => MaterialIconMap.Resolve("stop"),
        "reload" => MaterialIconMap.Resolve("sync"),
        _ => OmniIconName.Refresh,
    };

    private string ServiceLabel(string unit, string verb) =>
        string.Format(CultureInfo.CurrentCulture, L["MailTaskServiceAction"], L[unit == "postfix" ? "Postfix" : "Dovecot"],
            L[verb switch { "start" => "Start", "stop" => "Stop", "reload" => "Reload", _ => "Restart" }]);

    private Task ServiceActionAsync(string unit, string verb) =>
        QueueActionsAsync(unit == "postfix" ? MailTaskKind.Postfix : MailTaskKind.Dovecot, ServiceLabel(unit, verb), [ServiceAction(unit, verb)]);

    private Task StartServicesAsync() =>
        QueueActionsAsync(MailTaskKind.StartServices, L["MailTaskStartServices"], StartActions(StatusItems.FirstOrDefault(item => item.Key == "services")?.Detail ?? string.Empty));

    private Task RestartOpenDkimAsync() => QueueActionsAsync(MailTaskKind.OpenDkim, L["MailRestartOpenDkim"], [MailAction.RestartOpenDkim]);

    private async Task QueueActionsAsync(MailTaskKind kind, string label, IReadOnlyList<MailAction> actions)
    {
        if (actions.Count == 0 || IsBusy(kind)) return;
        _queuing.Add(kind);
        try
        {
            var queued = 0;
            foreach (var action in actions)
            {
                if (await Api.Mail.QueueMailActionAsync(ServerId, new MailActionRequest { Action = action }) is { } task)
                {
                    Follow(task.TaskId, kind, label);
                    queued++;
                }
            }
            if (queued == actions.Count) Toast.Success(L["TaskQueued"]);
            else Toast.Error(L["ActionFailed"]);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail action failed for server {ServerId}", ServerId);
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _queuing.Remove(kind);
        }
    }

    /// <summary>Queues one task through <paramref name="queue"/> and follows it; null from the API is a refusal.
    /// True when the task was queued: the caller says so.</summary>
    private async Task<bool> QueueTaskAsync(MailTaskKind kind, string label, Func<Task<MailTaskQueuedDto?>> queue)
    {
        _queuing.Add(kind);
        try
        {
            var queued = await queue();
            if (queued is null)
            {
                Toast.Error(L["ActionFailed"]);
                return false;
            }
            Follow(queued.TaskId, kind, label);
            return true;
        }
        finally
        {
            _queuing.Remove(kind);
        }
    }

    private async Task ObtainCertificateAsync()
    {
        if (IsBusy(MailTaskKind.ObtainCertificate)) return;
        var result = await Dialog.OpenAsync<MailOperationDialog>(L["MailObtainCertificate"],
            new Dictionary<string, object?>
            {
                { "Mode", MailDialogMode.RequestCertificate },
                { "Model", new MailDialogModel { Hostname = Mail.Hostname, Email = DefaultSender } }
            },
            new OmniDialogOptions { Width = "34rem", AutoFocusFirstElement = false });
        if (result is not MailDialogModel model) return;
        if (await QueueTaskAsync(MailTaskKind.ObtainCertificate, L["MailObtainCertificate"],
            () => Api.Mail.RequestMailCertificateAsync(ServerId, new RequestMailCertificateRequest { Email = model.AcmeEmail })))
            Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    private async Task InstallCertificateAsync()
    {
        if (IsBusy(MailTaskKind.InstallCertificate)) return;
        if (await QueueTaskAsync(MailTaskKind.InstallCertificate, L["MailInstallCertificate"],
            () => Api.Mail.InstallMailCertificateAsync(ServerId)))
            Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    private async Task RunServerCheckAsync()
    {
        if (IsBusy(MailTaskKind.ServerCheck)) return;
        _checks = [];
        if (await QueueTaskAsync(MailTaskKind.ServerCheck, L["MailRunServerCheck"],
            () => Api.Mail.QueueMailActionAsync(ServerId, new MailActionRequest { Action = MailAction.TestConfig })))
            Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    private async Task SendTestAsync()
    {
        if (IsBusy(MailTaskKind.DeliveryTest)) return;
        var result = await Dialog.OpenAsync<MailOperationDialog>(L["MailSendTest"],
            new Dictionary<string, object?>
            {
                { "Mode", MailDialogMode.TestDelivery },
                { "Model", new MailDialogModel { Source = DefaultSender } },
                { "Accounts", await ReadAccountsAsync() }
            },
            new OmniDialogOptions { Width = "32rem", AutoFocusFirstElement = false });
        if (result is not MailDialogModel model) return;

        _delivery = null;
        if (await QueueTaskAsync(MailTaskKind.DeliveryTest, L["MailSendTest"],
            () => Api.Mail.SendMailTestAsync(ServerId, new MailTestDeliveryRequest { From = model.Source, To = model.Destination })))
            Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    // Recette R3-47: the accounts of the server are the senders of the delivery test; unread, the dialog falls back to a typed address.
    private async Task<IReadOnlyList<MailAccountDto>> ReadAccountsAsync()
    {
        try
        {
            return await Api.Mail.GetMailAccountsAsync(ServerId);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail accounts read failed for the delivery test of server {ServerId}", ServerId);
            return [];
        }
    }

    private async Task MeasureUsageAsync()
    {
        if (IsBusy(MailTaskKind.Usage)) return;
        if (await QueueTaskAsync(MailTaskKind.Usage, L["MailMeasureUsage"], () => Api.Mail.RefreshMailQuotaAsync(ServerId)))
            Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    /// <summary>
    /// Called by the mail section for every completed task of the server. A task this list followed
    /// leaves the banner, frees its button, shows its outcome, and the state is read again at once.
    /// </summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!_running.Remove(notification.TaskId, out var task)) return;
        switch (task.Kind)
        {
            case MailTaskKind.ServerCheck:
                _checks = MailTaskOutputParser.ParseChecks(await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId));
                if (_checks.Count == 0) Toast.Error(L["ActionFailed"]);
                break;
            case MailTaskKind.DeliveryTest:
                // The helper exits non-zero for deferred/bounced mail but still prints the DELIVERY line.
                _delivery = MailTaskOutputParser.ParseDelivery(await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId))
                    ?? new MailDeliveryResultDto { Status = notification.Status.ToString(), Detail = L["ActionFailed"] };
                break;
            case MailTaskKind.Usage when notification.Status == TaskExecutionStatus.Success:
                var updated = await Api.Mail.IngestMailQuotaAsync(ServerId, notification.TaskId);
                Toast.Success("MailMeasureUsage", "MailUsageUpdated", updated);
                await OnUsageMeasured.InvokeAsync();
                break;
            case MailTaskKind.Usage:
                Toast.Error(L["ActionFailed"]);
                break;
            default:
                if (notification.Status != TaskExecutionStatus.Success)
                {
                    _failure = string.Format(CultureInfo.CurrentCulture, L["MailTaskFailedHeadline"], task.Label,
                        L.Localize(notification.Status));
                    // Recette R3-44: the end of the log, where the helper prints why a service did not start.
                    _failureLog = await TailAsync(notification.TaskId);
                }
                break;
        }
        await InvokeAsync(ReloadFromPushAsync);
    }

    private async Task<string> TailAsync(int taskId)
    {
        try
        {
            return await ServerTaskOutputLoader.TailAsync(Api, taskId, ServerTaskFollower.CauseLines) ?? L["MailReinstallNoLog"].Value;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail task log read failed for task {TaskId}", taskId);
            return L["MailReinstallNoLog"].Value;
        }
    }
}
