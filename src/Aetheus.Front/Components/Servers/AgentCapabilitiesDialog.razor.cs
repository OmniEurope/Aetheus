// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// R3-58 (ADR-051), R1-5 (ADR-055): the dialog that updates an agent and installs the whitelisted
/// components chosen here, instead of an operator running the installer's <c>--enable-*</c> flag over SSH.
/// It offers exactly <see cref="AgentEnableableCapabilities.Whitelist"/>, one card per component, and
/// updates the agent alone (which renews the helpers already installed) when nothing is chosen. It says
/// up front when the agent is too old to apply a component; the backend and the agent refuse anything else
/// again.
/// </summary>
public partial class AgentCapabilitiesDialog
{
    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private IReadOnlyList<string> _selected = [];
    private bool _busy;

    /// <summary>One card of the dialog: a whitelist token with its title, hint and icon.</summary>
    internal sealed record ComponentOption(string Token, string TitleKey, string HintKey, OmniIconName Icon);

    /// <summary>The cards, in whitelist order. A test pins that they are exactly the whitelist.</summary>
    internal static readonly IReadOnlyList<ComponentOption> Options =
    [
        new(AgentEnableableCapabilities.PatchManage, "WizardModulePatchManagement", "WizardModulePatchManagementHint", OmniIconName.ShieldCheck),
        new(AgentEnableableCapabilities.PackageManage, "AgentComponentPackageManage", "AgentComponentPackageManageHint", OmniIconName.Package),
        new(AgentEnableableCapabilities.ServiceEnable, "AgentComponentServiceEnable", "AgentComponentServiceEnableHint", OmniIconName.ToggleRight),
        new(AgentEnableableCapabilities.CronManage, "AgentComponentCronManage", "AgentComponentCronManageHint", OmniIconName.CalendarCheck),
        new(AgentEnableableCapabilities.RkhunterManage, "AgentComponentRkhunterManage", "AgentComponentRkhunterManageHint", OmniIconName.Bug),
        new(AgentEnableableCapabilities.ApacheManage, "AgentComponentApacheManage", "AgentComponentApacheManageHint", OmniIconName.Globe),
        new(AgentEnableableCapabilities.CertbotManage, "AgentComponentCertbotManage", "AgentComponentCertbotManageHint", OmniIconName.Lock),
        new(AgentEnableableCapabilities.PortsentryManage, "AgentComponentPortsentryManage", "AgentComponentPortsentryManageHint", OmniIconName.ShieldWarning),
        new(AgentEnableableCapabilities.FirewallManage, "WizardModuleFirewallManage", "WizardModuleFirewallManageHint", OmniIconName.Prohibit),
        new(AgentEnableableCapabilities.MailSetup, "AgentComponentMailSetup", "AgentComponentMailSetupHint", OmniIconName.Envelope),
        new(AgentEnableableCapabilities.TeamspeakSetup, "AgentComponentTeamspeakSetup", "AgentComponentTeamspeakSetupHint", OmniIconName.Broadcast)
    ];

    /// <summary>The installer's <c>--enable-*</c> flags exist on Linux only.</summary>
    internal static bool IsAvailableFor(ServerDetailDto server) =>
        OsTypeHelper.FromDescription(server.OsDescription) != OsType.Windows;

    /// <summary>Opens the dialog: the server menu (R1-5) and the Updates page share this one entry point.</summary>
    internal static Task OpenAsync(OmniDialogService dialog, IStringLocalizer<AppStrings> localizer, ServerDetailDto server) =>
        dialog.OpenAsync<AgentCapabilitiesDialog>(
            localizer["EnableAgentCapabilities"],
            new Dictionary<string, object?> { { "Server", server } },
            new OmniDialogOptions { Width = "560px", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });

    /// <summary>An agent that predates the field would update without applying the request.</summary>
    internal bool AgentCanEnable =>
        Server.AgentCapabilities.Contains(AgentCapabilities.EnableCapabilitiesOnUpdate, StringComparer.Ordinal);

    /// <summary>True when the server already reports the component: the card is shown as enabled and cannot be
    /// chosen again. Rkhunter reports nothing, so it is always offered (re-applying it is idempotent).</summary>
    internal bool IsGranted(string token) => token switch
    {
        AgentEnableableCapabilities.PatchManage => Server.PatchManagementAvailable == true || Reports(token),
        AgentEnableableCapabilities.PackageManage => Server.PackageManagementAvailable == true || Reports(token),
        AgentEnableableCapabilities.FirewallManage => Server.FirewallManagementAvailable == true || Reports(token),
        AgentEnableableCapabilities.MailSetup => Server.MailSetupAvailable || Reports(token),
        AgentEnableableCapabilities.TeamspeakSetup => Server.TeamspeakSetupAvailable || Reports(token),
        _ => Reports(token)
    };

    private bool Reports(string token) =>
        AgentCapabilities.ForEnableableToken(token) is { } capability
        && Server.AgentCapabilities.Contains(capability, StringComparer.Ordinal);

    /// <summary>Nothing chosen: the primary action only updates the agent, which renews the helpers it
    /// already installed. An old agent can only be updated that way.</summary>
    internal bool UpdatesAgentOnly => !AgentCanEnable || _selected.Count == 0;

    private string SubmitKey => UpdatesAgentOnly ? "EnableAgentCapabilitiesSubmitUpdate" : "EnableAgentCapabilitiesSubmit";

    internal bool CanSubmit => !_busy;

    private async Task SubmitAsync()
    {
        if (!CanSubmit) return;
        _busy = true;
        try
        {
            var updateOnly = UpdatesAgentOnly;
            var queued = updateOnly
                ? await Api.Servers.UpdateAgentAsync(Server.Id)
                : await Api.Servers.EnableAgentCapabilitiesAsync(Server.Id, _selected);
            if (queued is null)
            {
                Toast.Error("Error", updateOnly ? "UpdateAgentFailed" : "EnableAgentCapabilitiesFailed");
                return;
            }
            if (updateOnly)
                Toast.Success("UpdateAgent", "UpdateAgentQueued", Server.Name);
            else
                Toast.Success("EnableAgentCapabilities", "EnableAgentCapabilitiesQueued", Server.Name);
            Dialog.Close(queued);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Cancel() => Dialog.Close(null);
}
