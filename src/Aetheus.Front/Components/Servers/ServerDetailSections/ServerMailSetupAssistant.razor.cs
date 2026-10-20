// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>The mail wizard verifies DNS through the backend and outbound port 25 through an agent
/// probe before allowing setup. Derived hostname and administrator defaults follow the domain only
/// until edited by the user.</summary>
public partial class ServerMailSetupAssistant : IDisposable
{
    internal const int StepDomain = 0;
    internal const int StepARecord = 1;
    internal const int StepReverseDns = 2;
    internal const int StepOutbound = 3;
    internal const int StepConfigure = 4;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ILogger<ServerMailSetupAssistant> Logger { get; set; } = default!;
    [Inject] private TaskTrackerService Tracker { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>Postfix/Dovecot are present but no domain is configured: the intro says so.</summary>
    [Parameter] public bool StackPresent { get; set; }

    private readonly MailDialogModel _model = new() { Mode = MailDialogMode.Setup };
    private int _step;
    private IReadOnlyList<string> _stepErrors = [];

    // A record and reverse DNS: one backend call, valid for the host name it was made for.
    private MailDiagnosticsDto? _dns;
    private string _dnsCheckedHost = string.Empty;
    private bool _checkingDns;
    private bool _dnsCheckFailed;

    // Outbound port 25: an agent task.
    private int? _probeTaskId;
    private bool _probeQueuing;
    private MailComponentCheckDto? _outbound;

    // Current MX of the domain (kept from the former dialog: what the MX points at before switching).
    private MailMxPreviewDto? _mxPreview;
    private bool _checkingMx;
    private bool _mxCheckFailed;

    // From the click until the queued setup task completes, a second Configure starts nothing.
    private bool _setupStarting;
    private int? _setupTaskId;
    private TaskExecutionStatus? _setupFailure;

    internal int Step => _step;
    internal MailDialogModel Model => _model;
    internal bool SetupBusy => _setupStarting || _setupTaskId is not null;

    /// <summary>Whether <paramref name="taskId"/> is the setup this assistant queued (the section then counts again).</summary>
    internal bool OwnsSetupTask(int taskId) => taskId == _setupTaskId;

    private string ServerIp => Server.IpAddress;
    private string Host => _model.Hostname.Trim().ToLowerInvariant();
    private string Domain => _model.Domain.Trim().ToLowerInvariant();

    internal MailDiagnosticItemDto? ARecord => DnsItem("hostname-dns");
    internal MailDiagnosticItemDto? ReverseDns => DnsItem("reverse-dns");
    internal bool ARecordOk => ARecord?.Verdict == MailCheckVerdict.Ok;
    internal bool ReverseDnsOk => ReverseDns?.Verdict == MailCheckVerdict.Ok;
    internal bool OutboundOk => _outbound?.IsOk == true;
    internal bool PrerequisitesOk => ARecordOk && ReverseDnsOk && OutboundOk;
    internal bool CanConfigure => PrerequisitesOk && Server.MailSetupAvailable && !AgentOffline && !SetupBusy;

    // SITE-OFFLINE-001: the outbound probe and the setup are agent tasks, refused (409) while the agent is offline.
    private bool AgentOffline => AgentOfflineGuard.IsOffline(Server);

    private string ProbeTitle => AgentOffline ? L["AgentOfflineActionBlocked"].Value : L["MailCheckPrerequisite"].Value;

    private bool AgentCanProbe => Server.AgentCapabilities.Contains(AgentCapabilities.MailOutboundProbe, StringComparer.Ordinal);

    private bool CanCheckMx => Domain.Length is > 0 and <= 253 && MailValidation.IsValidDomainName(Domain);

    private bool PreviewMatchesDomain => _mxPreview is not null
        && _mxPreview.Domain.Equals(Domain, StringComparison.OrdinalIgnoreCase);

    private MailDiagnosticItemDto? DnsItem(string key) =>
        _dnsCheckedHost.Length > 0 && _dnsCheckedHost == Host ? _dns?.Items.FirstOrDefault(item => item.Key == key) : null;

    internal static string SuggestedHost(string domain) => domain.Length == 0 ? string.Empty : $"mx.{domain}";

    internal static string SuggestedAdmin(string domain) => domain.Length == 0 ? string.Empty : $"admin@{domain}";

    /// <summary>The domain fills the host name and the administrator address, unless the user typed their own.</summary>
    private void OnDomainChanged(string value)
    {
        var previous = Domain;
        _model.Domain = value;
        var domain = Domain;
        if (_model.Hostname.Length == 0 || _model.Hostname == SuggestedHost(previous))
            _model.Hostname = SuggestedHost(domain);
        if (_model.Email.Length == 0 || _model.Email == SuggestedAdmin(previous))
            _model.Email = SuggestedAdmin(domain);
    }

    private async Task OnStepChangedAsync(int step)
    {
        _step = step;
        _stepErrors = [];
        // Entering a DNS step checks it at once: the answer is what the step is about.
        if (step is StepARecord or StepReverseDns && DnsItem("hostname-dns") is null && !_checkingDns)
            await CheckDnsAsync();
    }

    /// <summary>The fields each step owns, validated by the model's own rules.</summary>
    internal static readonly IReadOnlyDictionary<int, string[]> StepMembers = new Dictionary<int, string[]>
    {
        [StepDomain] = [nameof(MailDialogModel.Hostname), nameof(MailDialogModel.Domain), nameof(MailDialogModel.DkimSelector)],
        [StepConfigure] = [nameof(MailDialogModel.Email), nameof(MailDialogModel.Password)]
    };

    // The model writes its messages through the page's localizer.
    protected override void OnInitialized()
    {
        _model.L = L;
        // Recette R3-46: a setup queued before the user left the page is still running on the agent;
        // the assistant finds it again in the live task feed and shows it running under its veil.
        AdoptOpenSetupTask();
        Tracker.OnChanged += OnTrackerChanged;
    }

    private void OnTrackerChanged()
    {
        if (AdoptOpenSetupTask()) _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>Binds to an in-flight setup task of this server, when the assistant follows none.</summary>
    internal bool AdoptOpenSetupTask()
    {
        if (SetupBusy) return false;
        var open = Tracker.Tasks.FirstOrDefault(task => task.ServerId == ServerId
            && task.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned or TaskExecutionStatus.Running
            && task.Name.StartsWith(MailTaskNames.SetupPrefix, StringComparison.Ordinal));
        if (open is null) return false;
        _setupTaskId = open.Id;
        _setupFailure = null;
        return true;
    }

    public void Dispose() => Tracker.OnChanged -= OnTrackerChanged;

    internal IReadOnlyList<string> StepErrors(int step)
    {
        if (!StepMembers.TryGetValue(step, out var members)) return [];
        // The length limits are read from the attributes and written in the user's language: the framework's
        // own message is English whatever the culture.
        var tooLong = members
            .Select(member => typeof(MailDialogModel).GetProperty(member)!)
            .Select(property => (Property: property, Limit: property.GetCustomAttributes(typeof(StringLengthAttribute), true)
                .Cast<StringLengthAttribute>().FirstOrDefault()))
            .Where(item => item.Limit is not null && (item.Property.GetValue(_model) as string)?.Length > item.Limit.MaximumLength)
            .Select(item => L["Validation_MaxLength", L[item.Property.Name].Value, item.Limit!.MaximumLength].Value);
        var rules = _model.Validate(new ValidationContext(_model))
            .Where(result => result.MemberNames.Any(members.Contains))
            .Select(result => result.ErrorMessage ?? L["Required"].Value);
        return [.. tooLong.Concat(rules).Distinct(StringComparer.Ordinal)];
    }

    private Task<bool> ValidateStepAsync(int step)
    {
        _stepErrors = StepErrors(step);
        return Task.FromResult(_stepErrors.Count == 0);
    }

    private async Task CheckDnsAsync()
    {
        var host = Host;
        if (!MailValidation.IsValidDomainName(host)) return;
        _checkingDns = true;
        _dnsCheckFailed = false;
        try
        {
            var result = await Api.Mail.CheckMailPrerequisitesAsync(ServerId, host);
            _dns = result;
            _dnsCheckedHost = result is null ? string.Empty : host;
            _dnsCheckFailed = result is null;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail prerequisites check failed for server {ServerId}", ServerId);
            _dnsCheckFailed = true;
        }
        finally
        {
            _checkingDns = false;
        }
    }

    private async Task ProbeOutboundAsync()
    {
        if (_probeTaskId is not null || _probeQueuing || !AgentCanProbe || AgentOfflineGuard.Blocks(Server, Toast)) return;
        _probeQueuing = true;
        _outbound = null;
        try
        {
            var queued = await Api.Mail.ProbeMailOutboundSmtpAsync(ServerId);
            if (queued is null) Toast.Error(L["ActionFailed"]);
            else _probeTaskId = queued.TaskId;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Outbound SMTP probe could not be queued for server {ServerId}", ServerId);
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _probeQueuing = false;
        }
    }

    private async Task CheckMxAsync()
    {
        if (!CanCheckMx) return;
        _checkingMx = true;
        _mxCheckFailed = false;
        _mxPreview = null;
        try
        {
            _mxPreview = await Api.Mail.PreviewMailMxAsync(ServerId, Domain);
            _mxCheckFailed = _mxPreview is null;
        }
        catch (HttpRequestException)
        {
            _mxCheckFailed = true;
        }
        finally
        {
            _checkingMx = false;
        }
    }

    private async Task ConfigureAsync()
    {
        if (SetupBusy || AgentOfflineGuard.Blocks(Server, Toast) || !CanConfigure) return;
        _setupStarting = true;
        _setupFailure = null;
        try
        {
            var queued = await Api.Mail.SetupMailAsync(ServerId, new MailSetupRequest
            {
                Hostname = Host,
                Domain = Domain,
                DkimSelector = _model.DkimSelector.Trim(),
                AdminEmail = _model.Email.Trim(),
                AdminPassword = _model.Password,
                QuotaMb = _model.QuotaMb,
                EnableSpamFilter = _model.EnableSpamFilter
            });
            if (queued is not null)
            {
                _setupTaskId = queued.TaskId;
                Toast.Success(L["TaskQueued"]);
            }
            else
            {
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Mail setup failed for server {ServerId}", ServerId);
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _setupStarting = false;
        }
    }

    /// <summary>Called by the mail section for every completed task of the server.</summary>
    public async Task HandleTaskCompletedAsync(TaskCompletedNotification notification)
    {
        if (notification.TaskId == _probeTaskId)
        {
            _probeTaskId = null;
            var output = await ServerTaskOutputLoader.ReadAsync(Api, notification.TaskId);
            // No CHECK line (agent refused the target, task timed out) is a failure, never a pass.
            _outbound = MailTaskOutputParser.ParseChecks(output)
                    .FirstOrDefault(check => check.Component == MailOutboundProbe.CheckComponent)
                ?? new MailComponentCheckDto
                {
                    Component = MailOutboundProbe.CheckComponent,
                    IsOk = false,
                    Detail = L.Localize(notification.Status)
                };
        }
        else if (notification.TaskId == _setupTaskId)
        {
            _setupTaskId = null;
            if (notification.Status != TaskExecutionStatus.Success) _setupFailure = notification.Status;
        }
        else
        {
            return;
        }
        await InvokeAsync(StateHasChanged);
    }
}
