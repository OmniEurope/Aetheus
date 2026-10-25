// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerFirewallSection
{
    [Parameter] public int ServerId { get; set; }

    /// <summary>From the server DTO - whether the agent can mutate ufw rules. Null = unknown.</summary>
    [Parameter] public bool? FirewallManagementAvailable { get; set; }

    [Parameter] public EventCallback OnRefreshRequested { get; set; }

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private ServerFirewallDto? _firewall;
    private bool _loading = true;
    private bool _busy;
    private int? _loadedServerId;
    private int _loadGeneration;

    private readonly NewRuleModel _rule = new();
    private enum FirewallRuleAction { Allow, Deny }

    private IReadOnlyList<OmniOption<FirewallRuleAction>> RuleActionOptions =>
    [
        new(FirewallRuleAction.Allow, L["FirewallAllow"]),
        new(FirewallRuleAction.Deny, L["FirewallDeny"]),
    ];

    private static OmniIconName RuleActionIcon(FirewallRuleAction action) =>
        action == FirewallRuleAction.Allow ? OmniIconName.LockOpen : OmniIconName.Prohibit;

    private static readonly string[] Protocols = ["tcp", "udp"];

    private sealed class NewRuleModel
    {
        [Range(1, 65535)]
        public int Port { get; set; } = 8080;

        public string Protocol { get; set; } = "tcp";

        [Required]
        [StringLength(64)]
        public string Source { get; set; } = "any";

        public FirewallRuleAction Action { get; set; } = FirewallRuleAction.Allow;
    }

    private bool CanManage => FirewallManagementAvailable == true;

    private OmniDataGrid<FirewallRuleDto>? _grid;

    // The Action filter shows the same words as the cells.
    private Func<string, string>? _actionText;
    private Func<string, string> ActionText => _actionText ??= LocalizeAction;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId) return;
        _loadedServerId = ServerId;
        await LoadAsync();
    }

    /// <summary> Reads the firewall state. <paramref name="live"/> is the task-completion path: the
    /// grid stays on screen (no loader) and is told before the new rules arrive, so the rules it
    /// did not hold read bold. </summary>
    private async Task LoadAsync(bool live = false)
    {
        var serverId = ServerId;
        var generation = Interlocked.Increment(ref _loadGeneration);
        if (!live) _loading = true;
        try
        {
            var firewall = await Api.Security.GetFirewallAsync(serverId);
            if (serverId == ServerId && generation == _loadGeneration)
            {
                if (live && _grid is not null) await _grid.RefreshAsync();
                _firewall = firewall;
            }
        }
        catch (HttpRequestException)
        {
            if (serverId == ServerId && generation == _loadGeneration)
                _firewall = null;
        }
        finally
        {
            if (serverId == ServerId && generation == _loadGeneration)
                _loading = false;
        }
    }

    public async Task HandleTaskCompletedAsync(TaskCompletedNotification _)
    {
        await LoadAsync(live: true);
        StateHasChanged();
    }

    private Task SubmitRuleAsync(NewRuleModel rule) => rule.Action == FirewallRuleAction.Allow
        ? RunAsync(() => Api.Security.FirewallAllowAsync(ServerId, BuildRequest()), "FirewallRuleQueued")
        : RunAsync(() => Api.Security.FirewallDenyAsync(ServerId, BuildRequest()), "FirewallRuleQueued");

    private string LocalizeAction(string action) => action.ToLowerInvariant() switch
    {
        "allow" => L["FirewallAllow"],
        "deny" => L["FirewallDeny"],
        _ => string.Format(L["FirewallUnknownAction"], action)
    };

    private async Task ToggleAsync(bool enable)
    {
        if (!enable)
        {
            var confirmed = await Dialog.Confirm(L["FirewallDisableConfirm"].Value, L["FirewallToggle"].Value,
                new OmniConfirmOptions { Destructive = true, OkButtonText = L["FirewallDisable"].Value, CancelButtonText = L["GoBack"].Value });
            if (confirmed != true) return;
        }
        await RunAsync(() => Api.Security.FirewallToggleAsync(ServerId, enable), enable ? "FirewallEnableQueued" : "FirewallDisableQueued");
    }

    private async Task DeleteRuleAsync(FirewallRuleDto rule)
    {
        if (rule.Protocol is not ("tcp" or "udp"))
        {
            Toast.Error("FirewallActionFailed", "FirewallActionFailed");
            return;
        }

        var confirmed = await Dialog.Confirm(
            string.Format(L["FirewallDeleteConfirm"].Value, rule.Raw), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await RunAsync(() => Api.Security.FirewallDeleteRuleAsync(ServerId, new FirewallRuleRequest
        {
            Port = rule.Port ?? 0,
            Protocol = rule.Protocol,
            Source = string.Equals(rule.Source, "Anywhere", StringComparison.OrdinalIgnoreCase) ? "any" : rule.Source
        }), "FirewallRuleQueued");
    }

    private FirewallRuleRequest BuildRequest() => new()
    {
        Port = _rule.Port,
        Protocol = _rule.Protocol,
        Source = string.IsNullOrWhiteSpace(_rule.Source) ? "any" : _rule.Source.Trim()
    };

    private async Task RunAsync(Func<Task<int?>> action, string successKey)
    {
        _busy = true;
        try
        {
            var taskId = await action();
            if (taskId is not null)
                Toast.Success(successKey, successKey);
            else
                Toast.Error("FirewallActionFailed", "FirewallActionFailed");
        }
        catch (HttpRequestException)
        {
            Toast.Error("FirewallActionFailed", "FirewallActionFailed");
        }
        finally
        {
            _busy = false;
        }
    }

    // Anti-lockout affordance: never offer to delete the SSH admin port from the UI (the backend + helper
    // refuse it anyway, but hiding the button avoids a confusing rejection).
    private static bool IsDeletable(FirewallRuleDto rule)
        => rule.Port is > 0 and not 22;
}
