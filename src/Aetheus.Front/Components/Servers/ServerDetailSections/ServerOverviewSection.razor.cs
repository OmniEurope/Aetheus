// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerOverviewSection : IDisposable
{
    // The three states a scanner diagnostic reads as, which the Status column also filters on.
    private const string ScannerReady = "ready";
    private const string ScannerOnDemand = "on-demand";
    private const string ScannerUnavailable = "unavailable";
    private static readonly IReadOnlyList<string> ScannerStates = [ScannerReady, ScannerOnDemand, ScannerUnavailable];

    internal static string ScannerStateOf(string value) => value.Contains(":degraded:", StringComparison.OrdinalIgnoreCase)
        || value.Contains(":unavailable:", StringComparison.OrdinalIgnoreCase)
        ? ScannerUnavailable
        : value.Contains(":available-on-demand:", StringComparison.OrdinalIgnoreCase) ? ScannerOnDemand : ScannerReady;

    /// <summary>A diagnostic matches the Status filter when its state is one of the ticked ones.</summary>
    private static readonly Func<string, string, bool> MatchesScannerState =
        (diagnostic, filter) => OmniDataGridFilterValues.Split(filter).Contains(ScannerStateOf(diagnostic));

    private Func<string, string>? _scannerStateText;
    private Func<string, string> ScannerStateText => _scannerStateText ??= state => state switch
    {
        ScannerUnavailable => L["AnalysisScannerUnavailable"],
        ScannerOnDemand => L["AnalysisScannerOnDemand"],
        _ => L["AnalysisScannerReady"]
    };

    private string ScannerDiagnosticState(string value) => ScannerStateText(ScannerStateOf(value));

    // Both columns are templates; the export writes the state and the diagnostic line.
    private Func<string, object?>? _scannerStateExport;
    private Func<string, object?> ScannerStateExport => _scannerStateExport ??= ScannerDiagnosticState;
    private static readonly Func<string, object?> ScannerDetailsExport = diagnostic => diagnostic;

    private static OmniTone ScannerDiagnosticStyle(string value) => ScannerStateOf(value) switch
    {
        ScannerUnavailable => OmniTone.Danger,
        ScannerOnDemand => OmniTone.Warning,
        _ => OmniTone.Success
    };

    // R1-32: the tabs of the page, the essentials first; the slug is the ?tab= value.
    internal static readonly string[] TabSlugs = ["essential", "storage", "runner", "ssh", "tasks"];

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;
    [Parameter] public bool MetricsReceived { get; set; }
    /// <summary>The initial detail load is over: a metric still missing is unavailable, no longer loading.</summary>
    [Parameter] public bool MetricsLoadCompleted { get; set; }
    [Parameter] public IReadOnlyList<ServerMetricDto> Metrics { get; set; } = [];
    // TC8R: toolchain capability lives on the heartbeat DTO (not ServerDetailDto); the loader surfaces
    // the latest value. Null = unknown (no heartbeat yet / legacy agent).
    [Parameter] public bool? PipelineRunnerAvailable { get; set; }
    [Parameter] public DateTime? LastUpdated { get; set; }
    [Parameter] public EventCallback<ServerDetailDto> ServerChanged { get; set; }

    // PLAN-010 lot 1: the agent's clock against the backend's, with the bound of the measure. When
    // the bound covers zero no direction is claimed; no measure reads as unknown, never as in sync.
    private string ClockOffsetText => Server is { ClockOffsetMs: { } offset, ClockOffsetUncertaintyMs: { } uncertainty }
        ? Math.Abs(offset) <= uncertainty
            ? L["ClockOffsetInSync", FormatClockDuration(uncertainty)]
            : L[offset > 0 ? "ClockOffsetAhead" : "ClockOffsetBehind",
                FormatClockDuration(Math.Abs(offset)), FormatClockDuration(uncertainty)]
        : L["ClockOffsetUnknown"];

    private static string FormatClockDuration(double milliseconds) => milliseconds < 1000
        ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{milliseconds:0} ms")
        : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{milliseconds / 1000:0.0} s");
    // PLAN-010 lot 1: OOM kills since boot under the memory figure. Windows has no such counter (not
    // applicable); a Linux agent that does not report it reads unknown, never "none".
    private string OomKillText => L["OomKillsSinceBoot", (Server.OomKillsSinceBoot ?? 0).ToString("N0", System.Globalization.CultureInfo.CurrentCulture)];

    private bool _runnerBusy;
    private bool _isolationBusy;

    private bool CanAdmin => Permissions.CanAdmin(ResourceType.Server, Server.Id);

    /// <summary>The agent sent a storage collection; DateTime.MinValue (default) is its "never collected".</summary>
    private bool StorageCollected => Server.StorageDiagnostics.CollectedAtUtc != default;

    private long? BuildCacheGrowthBytes
    {
        get
        {
            var points = Metrics
                .Where(metric => metric.Timestamp != default && metric.BuildCacheAvailable)
                .OrderBy(metric => metric.Timestamp)
                .ToList();
            return points.Count < 2 ? null : points[^1].BuildCacheBytes - points[0].BuildCacheBytes;
        }
    }

    // MainLayout loads permissions in parallel with route activation, so this section can
    // render before Permissions.IsLoaded - re-render when they land so the admin-gated
    // runner/isolation toggles reactivate. (Canonical pattern: Projects.razor.cs.)
    protected override void OnInitialized() => Permissions.OnPermissionsChanged += OnPermissionsChanged;

    private void OnPermissionsChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Permissions.OnPermissionsChanged -= OnPermissionsChanged;

    /// <summary>
    /// Flips PipelineRunnerEnabled. On enable, requires explicit confirmation (the operator is
    /// authorising arbitrary shell execution from any pipeline whose tag/pool resolves here).
    /// On disable, no confirmation - disabling is always safe.
    /// </summary>
    private async Task TogglePipelineRunnerAsync()
    {
        if (_runnerBusy) return;
        var newValue = !(Server.PipelineRunnerEnabled ?? false);

        if (newValue)
        {
            var confirmed = await Dialog.Confirm(
                string.Format(L["PipelineRunnerEnableConfirmBody"], Server.Name),
                L["PipelineRunnerEnableConfirmTitle"],
                new OmniConfirmOptions { OkButtonText = L["Enable"], CancelButtonText = L["GoBack"] });
            if (confirmed != true) return;
        }

        _runnerBusy = true;
        try
        {
            var updated = await Api.Servers.SetPipelineRunnerEnabledAsync(Server.Id, newValue);
            if (updated is null)
            {
                Toast.Error(L["ActionFailed"]);
                return;
            }

            // Reflect the new value locally; the SignalR `ServerUpdated` broadcast will also
            // refresh siblings, but this gives an immediate, optimistic UX.
            Server = Server with { PipelineRunnerEnabled = updated.PipelineRunnerEnabled };
            if (ServerChanged.HasDelegate)
                await ServerChanged.InvokeAsync(Server);

            Toast.Success(updated.PipelineRunnerEnabled == true
                ? L["PipelineRunnerEnabledToast"]
                : L["PipelineRunnerDisabledToast"]);
        }
        finally
        {
            _runnerBusy = false;
        }
    }

    /// <summary>
    /// Flips RequireContainerIsolation. On enforce, confirms (every future process-mode step on
    /// this runner will be blocked). On relax, no confirmation.
    /// </summary>
    private async Task ToggleContainerIsolationAsync()
    {
        if (_isolationBusy) return;
        var newValue = !Server.RequireContainerIsolation;

        if (newValue)
        {
            var confirmed = await Dialog.Confirm(
                string.Format(L["ContainerIsolationEnforceConfirmBody"], Server.Name),
                L["ContainerIsolationEnforceConfirmTitle"],
                new OmniConfirmOptions { OkButtonText = L["Enforce"], CancelButtonText = L["GoBack"] });
            if (confirmed != true) return;
        }

        _isolationBusy = true;
        try
        {
            var updated = await Api.Servers.SetContainerIsolationRequiredAsync(Server.Id, newValue);
            if (updated is null)
            {
                Toast.Error(L["ActionFailed"]);
                return;
            }

            Server = Server with { RequireContainerIsolation = updated.RequireContainerIsolation };
            if (ServerChanged.HasDelegate)
                await ServerChanged.InvokeAsync(Server);

            Toast.Success(updated.RequireContainerIsolation
                ? L["ContainerIsolationEnforcedToast"]
                : L["ContainerIsolationRelaxedToast"]);
        }
        finally
        {
            _isolationBusy = false;
        }
    }

    private static string FormatAgentVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "-";
        var parts = version.Split('.');
        return parts.Length >= 4
            ? $"v{string.Join(".", parts.Take(3))}"
            : $"v{version}";
    }

    /// <summary>The target version the agent should be updated to, or null when no update is due.</summary>
    private string? AgentUpdateTarget =>
        Server.AgentCompatibility is
        {
            Status: AgentCompatibilityStatus.UpdateRecommended or AgentCompatibilityStatus.UpdateRequired,
            TargetVersion: { Length: > 0 } target
        }
        && !string.Equals(Server.AgentVersion, target, StringComparison.OrdinalIgnoreCase)
            ? target
            : null;

    /// <summary>An update is due, so the Overview offers it under the version.</summary>
    internal bool AgentUpdateAvailable => AgentUpdateTarget is not null;

    private bool CanWrite => Permissions.CanWrite(ResourceType.Server, Server.Id);

    private bool _agentUpdateBusy;

    // The update this view queued, until the agent reports its version or it fails.
    private string? _requestedAgentTarget;
    private int? _requestedAgentUpdateId;

    internal AgentUpdateOverviewState AgentUpdateState =>
        AgentUpdateOverviewState.Read(Server, _requestedAgentTarget, _requestedAgentUpdateId);

    // SITE-OFFLINE-001: the agent update is a task only the agent can claim; the status badge above says
    // the agent is offline, the button waits for it. The runner and isolation toggles are server settings.
    private bool AgentOffline => AgentOfflineGuard.IsOffline(Server);

    /// <summary>The button stays while an update runs, busy, even if the compatibility read changes meanwhile.</summary>
    private bool ShowAgentUpdateButton => CanWrite && (AgentUpdateAvailable || AgentUpdateState.InProgress);

    private async Task UpdateAgentAsync()
    {
        if (_agentUpdateBusy || AgentUpdateState.InProgress) return;
        if (AgentOfflineGuard.Blocks(Server, Toast)) return;
        var target = AgentUpdateTarget;
        _agentUpdateBusy = true;
        try
        {
            var queued = await new AgentUpdateRequester(Api, Dialog, Toast, L).RequestAsync(Server);
            if (queued is not null)
            {
                _requestedAgentTarget = string.IsNullOrWhiteSpace(queued.TargetVersion) ? target : queued.TargetVersion;
                _requestedAgentUpdateId = queued.RequestId;
            }
        }
        finally
        {
            _agentUpdateBusy = false;
        }
    }

    private string FormatAgentVersionWithTarget()
    {
        var installed = FormatAgentVersion(Server.AgentVersion);
        return AgentUpdateTarget is { } target
            ? string.Format(L["AgentVersionUpdateAvailable"], installed, target)
            : installed;
    }

    private static string FormatBytes(long bytes) => DisplayFormatting.Size(bytes);

    private static string FormatTrend(long? bytes) => bytes switch
    {
        null => "-",
        > 0 => $"+{FormatBytes(bytes.Value)}",
        < 0 => $"-{FormatBytes(Math.Abs(bytes.Value))}",
        _ => DisplayFormatting.Size(0)
    };
}
