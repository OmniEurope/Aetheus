// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// SITE-OFFLINE-001: an action of a server module section queues a task that only the server's agent
/// can claim. While the agent is not online the backend refuses it (409); the section says so up front
/// (<see cref="AgentOfflineNotice"/>), disables its actions, and its handlers stop here before calling
/// the API, the same rule the Services page already applies.
/// </summary>
internal static class AgentOfflineGuard
{
    internal static bool IsOffline(ServerDetailDto server) => server.Status != ServerStatus.Online;

    /// <summary>True, with the toast the Services page shows, when the action must not be sent.</summary>
    internal static bool Blocks(ServerDetailDto server, NotifyHelper toast)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(toast);
        if (!IsOffline(server)) return false;
        toast.Error("TaskAgentOffline", "AgentOfflineActionBlocked");
        return true;
    }
}
