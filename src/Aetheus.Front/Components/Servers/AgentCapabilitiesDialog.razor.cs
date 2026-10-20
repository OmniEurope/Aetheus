// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// R3-58 (ADR-051): the dialog that updates an agent and grants it the whitelisted privileges chosen
/// here, instead of an operator running the installer's <c>--enable-*</c> flag over SSH. It offers only
/// <see cref="AgentEnableableCapabilities.Whitelist"/> and says up front when the agent is too old to
/// apply the request; the backend and the agent refuse anything else again.
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

    /// <summary>An agent that predates the field would update without applying the request.</summary>
    internal bool AgentCanEnable =>
        Server.AgentCapabilities.Contains(AgentCapabilities.EnableCapabilitiesOnUpdate, StringComparer.Ordinal);

    internal bool PatchManageGranted => Server.PatchManagementAvailable == true;

    internal bool CanSubmit => !_busy && AgentCanEnable && _selected.Count > 0;

    private async Task SubmitAsync()
    {
        if (!CanSubmit) return;
        _busy = true;
        try
        {
            var queued = await Api.Servers.EnableAgentCapabilitiesAsync(Server.Id, _selected);
            if (queued is null)
            {
                Toast.Error("Error", "EnableAgentCapabilitiesFailed");
                return;
            }
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
