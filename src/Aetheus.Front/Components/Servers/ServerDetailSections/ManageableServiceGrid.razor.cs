// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Reusable manageable-services grid with row multi-select and a bulk-action bar. Drives both the
/// per-row actions (delegated to the host) and a single batched action over the checked rows
/// (<see cref="OnBulk"/>). Selection is tracked by service name so it survives the host re-creating
/// the <see cref="ServiceInfoDto"/> list on each heartbeat refresh.
/// </summary>
public partial class ManageableServiceGrid
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    internal static string FormatType(ServiceType type) => type switch
    {
        ServiceType.Systemd => "Systemd",
        ServiceType.Docker => "Docker",
        ServiceType.WindowsService => "Windows",
        _ => type.ToString()
    };

    /// <summary>
    /// Maps a service status string + IsRunning flag to a badge style. A oneshot/timer-driven
    /// service legitimately sits at rest with <c>scheduled</c> (paired .timer is active) or
    /// <c>idle</c> (cron-driven oneshot) - these are healthy states and must NOT show as Danger.
    /// </summary>
    internal static OmniTone GetStatusBadgeStyle(string status, bool isRunning) =>
        ServiceStatusPresentation.Tone(status, isRunning);

    /// <summary>The status in the reader's language; a word the page does not know is shown as the
    /// host wrote it.</summary>
    private string StatusText(string status, bool isRunning) =>
        ServiceStatusPresentation.LabelKey(status, isRunning) is { } key ? L[key] : status;

    private Func<string, string>? _statusFilterText;
    private Func<string, string> StatusFilterText => _statusFilterText ??= status => StatusText(status, isRunning: false);

    [Parameter, EditorRequired] public IEnumerable<ServiceInfoDto> Services { get; set; } = [];

    /// <summary>true = installed grid (start/stop/restart/uninstall); false = available grid (install).</summary>
    [Parameter] public bool Installed { get; set; }

    /// <summary>Installed grid only: render the service name as an "open module" link instead of a logs link.</summary>
    [Parameter] public bool ShowModuleLink { get; set; }

    [Parameter] public bool CanManagePackages { get; set; }

    /// <summary>S-UX-OFFA: the target server is online. When false, every mutating action
    /// (start/stop/restart/install/uninstall) is disabled up-front - no agent is polling to run it, so a
    /// click would only queue a task that sits pending. Logs/open-module stay enabled (read-only).</summary>
    [Parameter] public bool ServerOnline { get; set; } = true;

    private string ActionTitle(string enabledTitle) => ServerOnline ? enabledTitle : L["AgentOfflineActionBlocked"];

    /// <summary>Whether the agent has the teamspeak-setup capability. TeamSpeak installs via its own
    /// root-owned helper (not apt), so its Install button gates on this rather than CanManagePackages.</summary>
    [Parameter] public bool CanInstallTeamspeak { get; set; }

    /// <summary>The action in flight or just finished on a service, by its name.</summary>
    [Parameter] public Func<string, ServiceActionState?>? ActionStateOf { get; set; }

    private ServiceActionState? ActionOf(string serviceName) => ActionStateOf?.Invoke(serviceName);

    private bool IsBusy(string serviceName) => ActionOf(serviceName) is { InFlight: true };

    /// <summary>Recette R3-22: false when the page shows one banner for all its grids. A service being
    /// uninstalled moves from the installed grid to the available one, and a banner per grid moved with it.</summary>
    [Parameter] public bool ShowInFlightBanner { get; set; } = true;

    private string ActionText(ServiceActionState action) => action.Phase switch
    {
        ServiceActionPhase.Queued => string.Format(L["ServiceActionQueued"], L[action.ActionKey]),
        ServiceActionPhase.Running => string.Format(L["ServiceActionRunning"], L[action.ActionKey]),
        ServiceActionPhase.Succeeded => string.Format(L["ServiceActionSucceeded"], L[action.ActionKey]),
        _ => action.ExitCode is { } code
            ? string.Format(L["ServiceActionFailedExit"], L[action.ActionKey], code)
            : string.Format(L["ServiceActionFailed"], L[action.ActionKey])
    };

    /// <summary>On the grid of installed services with a module, the row opens it.</summary>
    private bool OpensModuleOnRowClick => Installed && ShowModuleLink;

    private Task OnRowClickedAsync(OmniDataGridRowMouseEventArgs<ServiceInfoDto> args) =>
        OpensModuleOnRowClick ? OnOpenModule.InvokeAsync(args.Item.Name) : Task.CompletedTask;

    [Parameter] public EventCallback<string> OnStart { get; set; }
    [Parameter] public EventCallback<string> OnStop { get; set; }
    [Parameter] public EventCallback<string> OnRestart { get; set; }
    [Parameter] public EventCallback<string> OnInstall { get; set; }
    [Parameter] public EventCallback<string> OnUninstall { get; set; }
    [Parameter] public EventCallback<string> OnLogs { get; set; }
    [Parameter] public EventCallback<string> OnOpenModule { get; set; }

    /// <summary>Fired with the action kind (Start/Stop/Restart/Install/Uninstall) and the checked names.</summary>
    [Parameter] public EventCallback<BulkServiceRequest> OnBulk { get; set; }

    /// <summary>The server shown, so a switch to another server does not read as new rows.</summary>
    [Parameter] public int ServerId { get; set; }

    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    // The Type filter lists the translated service types.
    private Func<string, string>? _typeText;
    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<ServiceType>(L);

    // Each heartbeat brings a new service list; services that were not there read bold.
    private OmniDataGrid<ServiceInfoDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Services, ServerId);

    private bool AllSelected => Services.Any() && Services.All(s => _selected.Contains(s.Name));

    private void ToggleAll(bool value)
    {
        _selected.Clear();
        if (value)
            foreach (var s in Services)
                _selected.Add(s.Name);
    }

    private void ToggleOne(string name, bool value)
    {
        if (value) _selected.Add(name);
        else _selected.Remove(name);
    }

    private void ClearSelection() => _selected.Clear();

    // Package-managed services and dedicated module helpers have distinct install capabilities.
    // Services supporting neither route are not offered for installation.
    internal static bool IsAptInstallable(string serviceName) =>
        Aetheus.Shared.Components.Servers.ManageablePackages.IsManageableService(serviceName);

    // Services installed via a dedicated module helper instead of apt. TeamSpeak is the only one today.
    internal static bool IsModuleInstallable(string serviceName) =>
        serviceName.Equals("teamspeak", StringComparison.OrdinalIgnoreCase);

    // Whether the Install action can be offered for this service given the server's capabilities.
    private bool CanInstall(string serviceName) =>
        !IsMailStackLocked(serviceName)
        && (IsModuleInstallable(serviceName) ? CanInstallTeamspeak : CanManagePackages && IsAptInstallable(serviceName));

    /// <summary>Recette R3-24/R3-25/R3-43: Postfix and Dovecot of a mail stack Aetheus manages are set up and
    /// removed whole from the Mail page; the backend refuses them here as well.</summary>
    [Parameter] public bool MailStackManaged { get; set; }

    internal bool IsMailStackLocked(string serviceName) =>
        MailStackManaged
        && (serviceName.Equals("postfix", StringComparison.OrdinalIgnoreCase)
            || serviceName.Equals("dovecot", StringComparison.OrdinalIgnoreCase));

    private string InstallTitle(string serviceName)
    {
        if (IsMailStackLocked(serviceName))
            return L["MailStackManagedServiceHint"];
        if (IsModuleInstallable(serviceName))
            return CanInstallTeamspeak ? L["Install"] : L["TeamspeakSetupCapabilityDisabled"];
        if (!CanManagePackages)
            return L["ServiceInstallCapabilityDisabled"];
        return IsAptInstallable(serviceName) ? L["Install"] : L["ServiceNotAptInstallable"];
    }

    private static void OnRowRenderInternal(OmniDataGridRowRenderArgs<ServiceInfoDto> args)
    {
        if (!args.Item.IsInstalled)
            args.Class = "omni-u-text-muted";
    }

    private async Task FireBulkAsync(string kind)
    {
        // Snapshot in the current data order so the batch matches what the user sees. For Install, drop
        // any checked service that can't actually be installed on this server (capability missing, or not
        // installable at all) so the batch can't report a confusing partial count for impossible actions.
        var names = Services
            .Where(s => _selected.Contains(s.Name))
            .Where(s => kind != "Install" || CanInstall(s.Name))
            .Select(s => s.Name)
            .ToList();
        if (names.Count == 0) return;
        await OnBulk.InvokeAsync(new BulkServiceRequest(kind, names));
        _selected.Clear();
    }
}

/// <summary>A batched service action requested from the grid's bulk bar.</summary>
public sealed record BulkServiceRequest(string Kind, IReadOnlyList<string> Services);
