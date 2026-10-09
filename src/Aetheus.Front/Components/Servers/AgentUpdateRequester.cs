// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

/// <summary>
/// The update of one server's agent: a confirmation naming the installed and target versions, then
/// the per-server update call. Asked from the Tools menu of the server header and, recette R2-033, from
/// the agent version on the Overview when an update is available.
/// </summary>
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
