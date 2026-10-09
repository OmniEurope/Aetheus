// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R2-090: the state of the mail stack the page opens on. The backend checklist (PLAN-005 lot 7) is
/// reduced to what makes mail work, in the order a fix depends on: services, the host name's A record, its
/// reverse DNS, the certificate (which needs the A record), then MX, SPF, DKIM and DMARC of each domain.
/// Each line is green or red; the first red line shows its fix (<see cref="MailStatusFix"/>). The server
/// check (<c>CHECK</c> lines), the delivery test (<c>DELIVERY</c> line) and the usage measurement are read
/// from the task they queued, as the former Diagnostics tab did.
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

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public MailDataDto Mail { get; set; } = new();
    [Parameter] public bool CanManage { get; set; }
    [Parameter] public string DefaultSender { get; set; } = string.Empty;

    /// <summary>Raised once measured mailbox usage has been stored, so the accounts grid reloads.</summary>
    [Parameter] public EventCallback OnUsageMeasured { get; set; }

    private MailDiagnosticsDto? _diagnostics;
    private bool _loading;
    private bool _busy;
    private int? _checkTaskId;
    private int? _deliveryTaskId;
    private int? _quotaTaskId;
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

    /// <summary>The first line that is not green: the one whose fix is shown.</summary>
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

    private Task ServiceActionAsync(string unit, string verb) => QueueActionsAsync([ServiceAction(unit, verb)]);

    private Task StartServicesAsync() => QueueActionsAsync(StartActions(FirstRed?.Detail ?? string.Empty));

    private Task RestartOpenDkimAsync() => QueueActionsAsync([MailAction.RestartOpenDkim]);

    private async Task QueueActionsAsync(IReadOnlyList<MailAction> actions)
    {
        if (actions.Count == 0) return;
        _busy = true;
        try
        {
            var queued = 0;
            foreach (var action in actions)
            {
                if (await Api.Mail.QueueMailActionAsync(ServerId, new MailActionRequest { Action = action }) is not null)
                    queued++;
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
            _busy = false;
        }
    }

    private async Task ObtainCertificateAsync()
    {
        var result = await Dialog.OpenAsync<MailOperationDialog>(L["MailObtainCertificate"],
            new Dictionary<string, object?>
            {
                { "Mode", MailDialogMode.RequestCertificate },
                { "Model", new MailDialogModel { Hostname = Mail.Hostname, Email = DefaultSender } }
            },
            new OmniDialogOptions { Width = "34rem", AutoFocusFirstElement = false });
        if (result is not MailDialogModel model) return;
        _busy = true;
        try
        {
            var queued = await Api.Mail.RequestMailCertificateAsync(ServerId, new RequestMailCertificateRequest { Email = model.Email });
            if (queued is not null) Toast.Success(L["TaskQueued"]);
            else Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task InstallCertificateAsync()
    {
        _busy = true;
        try
        {
            var queued = await Api.Mail.InstallMailCertificateAsync(ServerId);
            if (queued is not null) Toast.Success(L["TaskQueued"]);
            else Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunServerCheckAsync()
    {
        _checks = [];
        var queued = await Api.Mail.QueueMailActionAsync(ServerId, new MailActionRequest { Action = MailAction.TestConfig });
        if (queued is null)
        {
            Toast.Error(L["ActionFailed"]);
            return;
        }
        _checkTaskId = queued.TaskId;
        Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    private async Task SendTestAsync()
    {
        var result = await Dialog.OpenAsync<MailOperationDialog>(L["MailSendTest"],
            new Dictionary<string, object?>
            {
                { "Mode", MailDialogMode.TestDelivery },
                { "Model", new MailDialogModel { Source = DefaultSender } }
            },
            new OmniDialogOptions { Width = "32rem", AutoFocusFirstElement = false });
        if (result is not MailDialogModel model) return;

        _delivery = null;
        var queued = await Api.Mail.SendMailTestAsync(ServerId, new MailTestDeliveryRequest { From = model.Source, To = model.Destination });
        if (queued is null)
        {
            Toast.Error(L["ActionFailed"]);
            return;
        }
        _deliveryTaskId = queued.TaskId;
        Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    private async Task MeasureUsageAsync()
    {
        var queued = await Api.Mail.RefreshMailQuotaAsync(ServerId);
        if (queued is null)
        {
            Toast.Error(L["ActionFailed"]);
            return;
        }
        _quotaTaskId = queued.TaskId;
        Toast.Info("TaskQueued", "MailWaitingForTask");
    }

    /// <summary>Called by the mail section for every completed task of the server.</summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification notification)
    {
        if (notification.TaskId == _checkTaskId)
        {
            _checkTaskId = null;
            _checks = MailTaskOutputParser.ParseChecks(await MailTaskOutputLoader.ReadAsync(Api, notification.TaskId));
            if (_checks.Count == 0) Toast.Error(L["ActionFailed"]);
        }
        else if (notification.TaskId == _deliveryTaskId)
        {
            _deliveryTaskId = null;
            // The helper exits non-zero for deferred/bounced mail but still prints the DELIVERY line.
            _delivery = MailTaskOutputParser.ParseDelivery(await MailTaskOutputLoader.ReadAsync(Api, notification.TaskId))
                ?? new MailDeliveryResultDto { Status = notification.Status.ToString(), Detail = L["ActionFailed"] };
        }
        else if (notification.TaskId == _quotaTaskId)
        {
            _quotaTaskId = null;
            if (notification.Status == TaskExecutionStatus.Success)
            {
                var updated = await Api.Mail.IngestMailQuotaAsync(ServerId, notification.TaskId);
                Toast.Success("MailMeasureUsage", "MailUsageUpdated", updated);
                await OnUsageMeasured.InvokeAsync();
            }
            else
            {
                Toast.Error(L["ActionFailed"]);
            }
        }
        else
        {
            return;
        }
        await InvokeAsync(StateHasChanged);
    }
}
