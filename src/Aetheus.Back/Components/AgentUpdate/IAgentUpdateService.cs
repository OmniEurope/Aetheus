// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.AgentUpdate;

/// <summary>
/// Queues agent self-update operations. The backend never pushes binaries directly: it enqueues
/// an <c>OperationKind.AgentSelfUpdate</c> task that the target agent picks up on its next poll,
/// then downloads the latest build from <c>/downloads</c> and hands off to a detached platform
/// updater. Progress and the final outcome flow back over the existing task-log / SignalR channel.
/// </summary>
public interface IAgentUpdateService
{
    /// <summary>Queues a self-update for one server. Throws <c>NotFoundException</c> if it doesn't exist.</summary>
    Task<AgentUpdateResponse> QueueUpdateAsync(
        int serverId,
        string requestedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Queues a self-update that also grants the named capabilities on the host (R3-58, ADR-051). Only
    /// <see cref="AgentEnableableCapabilities.Whitelist"/> entries are accepted. Throws
    /// <c>BadRequestException</c> for an empty or non-whitelisted list or a non-Linux server,
    /// <c>NotFoundException</c> for an unknown server and <c>ConflictException</c> when the agent is too
    /// old to apply the request or an update already underway cannot take it.
    /// </summary>
    Task<AgentUpdateResponse> QueueUpdateWithCapabilitiesAsync(
        int serverId,
        string requestedBy,
        IReadOnlyCollection<string> capabilities,
        CancellationToken ct = default);

    /// <summary>
    /// Queues a self-update for every server in <paramref name="accessibleServerIds"/>
    /// (already filtered to the caller's administrable resources).
    /// </summary>
    Task<AgentUpdateAllResponse> QueueUpdateAllAsync(
        List<int>? accessibleServerIds,
        string requestedBy,
        CancellationToken ct = default);

    Task<AgentUpdateAllPreviewDto> PreviewUpdateAllAsync(
        List<int>? accessibleServerIds,
        CancellationToken ct = default);

    /// <summary>
    /// Forwards a progress phase reported by the agent (item #3 of the plan) to UI clients on
    /// the server group. Transient - nothing is persisted. The agent identity is already validated
    /// by the controller (JWT <c>ServerId</c> claim); this method only fans out.
    /// </summary>
    Task BroadcastProgressAsync(AgentUpdateProgressDto dto, CancellationToken ct = default);
}
