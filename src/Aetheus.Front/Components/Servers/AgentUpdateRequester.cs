// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

/// <summary>Confirms installed and target agent versions before requesting an update for one
/// server. Shared by the server Tools menu and the Overview update action.</summary>
internal sealed class AgentUpdateRequester(
    ApiClient api,
    OmniDialogService dialog,
    NotifyHelper toast,
    IStringLocalizer<AppStrings> localizer)
{
    /// <summary>Asks for the update; the queued request, or null when declined or refused.</summary>
    internal async Task<AgentUpdateResponse?> RequestAsync(ServerDetailDto server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var targetVersion = server.AgentCompatibility?.TargetVersion
            ?? server.AgentUpdateRequest?.TargetVersion
            ?? localizer["Unknown"].Value;
        var confirmed = await dialog.Confirm(
            string.Format(localizer["UpdateAgentConfirm"], server.Name, server.AgentVersion, targetVersion),
            localizer["UpdateAgent"],
            new OmniConfirmOptions { OkButtonText = localizer["Update"], CancelButtonText = localizer["GoBack"] });
        if (confirmed != true) return null;

        // No AlreadyUpToDate branch any more: the per-server action queues the update whatever version
        // the agent reports, so this only ever comes back queued or failed. Reinstalling a current agent
        // is a repair, and refusing it made the click do nothing on the one server being repaired.
        var result = await api.Servers.UpdateAgentAsync(server.Id);
        if (result is null)
        {
            toast.Error("Error", "UpdateAgentFailed");
            return null;
        }
        toast.Success("UpdateAgent", "UpdateAgentQueued", server.Name);
        return result;
    }
}
