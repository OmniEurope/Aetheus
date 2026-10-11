// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R2-090: the state of the mail stack the page opens on. The backend checklist (PLAN-005 lot 7) is
/// reduced to what makes mail work, in the order a fix depends on: services, the host name's A record, its
/// reverse DNS, the certificate (which needs the A record), then MX, SPF, DKIM and DMARC of each domain.
/// Each line is green or red; every red line shows its fix (<see cref="MailStatusFix"/>, recette R1-9). The server
/// check (<c>CHECK</c> lines), the delivery test (<c>DELIVERY</c> line) and the usage measurement are read
/// from the task they queued, as the former Diagnostics tab did. Recette R1-7 / R1-8 / R1-13: every task
/// the page queues is followed by its id until it completes: its button stays busy, one banner names it,
/// and its completion reads the state again at once.
/// </summary>
public partial class ServerMailStatusList : IAsyncDisposable
{
    internal const int CurrentHelperVersion = 3;

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
    private MailDeliveryResultDto? _delivery;
    private List<MailComponentCheckDto> _checks = [];

    internal MailDeliveryResultDto? Delivery => _delivery;
    internal IReadOnlyList<MailComponentCheckDto> Checks => _checks;

    // R-181: no Refresh button; reading the checklist queues no agent task (inventory + DNS), so completed
    // tasks can trigger a reload as safely as heartbeats.
    private ServerLiveFeed? _liveFeed;
    internal ServerLiveFeed? LiveFeed => _liveFeed;

    /// <summary>The lines of the status, in fix order.</summary>
    internal IReadOnlyList<MailDiagnosticItemDto> StatusItems => _diagnostics is null ? [] :
    [
        .. s_serverKeys.Select(key => _diagnostics.Items.FirstOrDefault(item => item.Key == key)).OfType<MailDiagnosticItemDto>(),
        .. _diagnostics.Items.Where(item => s_domainPrefixes.Any(prefix => item.Key.StartsWith(prefix, StringComparison.Ordinal)))
    ];

    /// <summary>Recette R1-11: the domain the DNS help names, the first one checked; null without a mail domain.</summary>
    internal string? HelpDomain => StatusItems.Select(item => item.Key).FirstOrDefault(key => key.StartsWith("mx:", StringComparison.Ordinal))?[3..];

    /// <summary>The first line that is not green (every red line shows its fix, recette R1-9).</summary>
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

    // ── Recette R1-7 / R1-8 / R1-13: every task the page queues is followed until it completes ──────

    /// <summary>What a followed task does: which button stays busy, and how its completion is read.</summary>
    internal enum MailTaskKind { Postfix, Dovecot, StartServices, OpenDkim, ObtainCertificate, InstallCertificate, ServerCheck, DeliveryTest, Usage }

    /// <summary>A queued task not completed yet: its kind, its name in the banner, when it was queued.</summary>
    internal sealed record RunningMailTask(MailTaskKind Kind, string Label, DateTimeOffset QueuedAt);

    /// <summary>The followed tasks by id, from the queuing answer until their completion.</summary>
    internal IReadOnlyDictionary<int, RunningMailTask> RunningTasks => _running;

    /// <summary>The outcome of the last failed action task (services, OpenDKIM, certificate), or null.</summary>
    internal string? Failure => _failure;

    /// <summary>The task kind is being queued or runs: its button is busy.</summary>
    internal bool IsBusy(MailTaskKind kind) => _queuing.Contains(kind) || _running.Values.Any(task => task.Kind == kind);

    private IReadOnlyList<string> RunningLabels => [.. _running.Values.OrderBy(task => task.QueuedAt).Select(task => task.Label).Distinct()];

    private DateTimeOffset RunningSince => _running.Values.Min(task => task.QueuedAt);

    private void Follow(int taskId, MailTaskKind kind, string label)
    {
        _failure = null;
        _running[taskId] = new RunningMailTask(kind, label, Clock.GetUtcNow());
    }

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
                { "Model", new MailDialogModel { Source = DefaultSender } }
            },
            new OmniDialogOptions { Width = "32rem", AutoFocusFirstElement = false });
        if (result is not MailDialogModel model) return;

        _delivery = null;
        if (await QueueTaskAsync(MailTaskKind.DeliveryTest, L["MailSendTest"],
            () => Api.Mail.SendMailTestAsync(ServerId, new MailTestDeliveryRequest { From = model.Source, To = model.Destination })))
            Toast.Info("TaskQueued", "MailWaitingForTask");
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
                _checks = MailTaskOutputParser.ParseChecks(await MailTaskOutputLoader.ReadAsync(Api, notification.TaskId));
                if (_checks.Count == 0) Toast.Error(L["ActionFailed"]);
                break;
            case MailTaskKind.DeliveryTest:
                // The helper exits non-zero for deferred/bounced mail but still prints the DELIVERY line.
                _delivery = MailTaskOutputParser.ParseDelivery(await MailTaskOutputLoader.ReadAsync(Api, notification.TaskId))
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
                    _failure = string.Format(CultureInfo.CurrentCulture, L["MailTaskFailed"], task.Label,
                        L.Localize(notification.Status), await LastLineAsync(notification.TaskId));
                break;
        }
        await InvokeAsync(ReloadFromPushAsync);
    }

    private async Task<string> LastLineAsync(int taskId)
    {
        try
        {
            return await MailTaskOutputLoader.LastLineAsync(Api, taskId) ?? L["MailReinstallNoLog"].Value;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail task log read failed for task {TaskId}", taskId);
            return L["MailReinstallNoLog"].Value;
        }
    }
}
