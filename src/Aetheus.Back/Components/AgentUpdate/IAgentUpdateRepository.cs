// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AgentUpdate;

public interface IAgentUpdateRepository
{
    Task<(AgentUpdateRequest Request, bool Created)> ReserveAsync(
        Server server,
        AgentReleaseManifestDto release,
        string requestedBy,
        CancellationToken ct,
        IReadOnlyList<string>? enableCapabilities = null);
    Task<(AgentUpdateRequest Request, ServerTask? CreatedTask, int BlockingTaskCount)> TryQueueAsync(
        int requestId,
        CancellationToken ct);
    Task<List<int>> GetWaitingRequestIdsAsync(CancellationToken ct);
    /// <summary>
    /// Pending tasks created before <paramref name="olderThan"/> ago whose server is online, polling
    /// and not mid-update, with what its agent declared. The caller decides which of them the agent can
    /// never claim; this is only the candidate set, bounded per call.
    /// </summary>
    Task<List<PendingTaskAgentContext>> GetPendingTasksOfPollingAgentsAsync(
        TimeSpan olderThan, CancellationToken ct);
    Task<AgentUpdateRequest?> FindActiveByServerAsync(int serverId, CancellationToken ct);
    Task<AgentUpdateRequest?> MarkProgressAsync(int serverId, AgentUpdateProgressDto progress, CancellationToken ct);
    Task<AgentUpdateRequest?> ConfirmFromHeartbeatAsync(
        int serverId,
        ServerHeartbeatDto heartbeat,
        CancellationToken ct);
    Task<List<AgentUpdateRequest>> FailExpiredAsync(CancellationToken ct);

    // Own-reads over Server: obtaining them by injecting IServerRepository put this module and
    // Servers in the same cycle.
    Task<List<Server>> GetServersForAgentUpdateAsync(List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<Server?> FindServerAsync(int id, CancellationToken ct = default);
    Task<List<ServerDto>> GetServersForCompatibilityAsync(List<int>? accessibleIds, CancellationToken ct = default);
    Task<int> CountActiveNonUpdateTasksAsync(int serverId, CancellationToken ct = default);

    /// <summary>Batch variant: one round trip for a whole fleet, keyed by server id.</summary>
    Task<IReadOnlyDictionary<int, int>> CountActiveNonUpdateTasksAsync(
        IReadOnlyCollection<int> serverIds, CancellationToken ct = default);
}
