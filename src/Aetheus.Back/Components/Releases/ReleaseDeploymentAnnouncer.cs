// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Releases;

/// <summary>Announces a release that a pipeline's <c>type: release</c> step recorded as deployed.</summary>
public interface IReleaseDeploymentAnnouncer
{
    /// <summary>Raises <see cref="ReleaseDeployedEvent"/> for the release, naming the stage the agent
    /// is running in that run so the target environment's deployment policy applies.</summary>
    Task AnnounceAsync(int releaseId, int pipelineRunId, int agentServerId, CancellationToken ct = default);
}

/// <summary>Announce deployment for release steps recorded with deployed: true, using the active
/// agent step to identify the stage. Branch advancement remains a separate explicit advance-branch
/// step.</summary>
public sealed class ReleaseDeploymentAnnouncer(
    IPipelineRepository pipelines,
    IDomainEventDispatcher domainEvents) : IReleaseDeploymentAnnouncer
{
    public async Task AnnounceAsync(int releaseId, int pipelineRunId, int agentServerId, CancellationToken ct = default)
    {
        var stageName = await pipelines.FindRunningStageNameAsync(pipelineRunId, agentServerId, ct).ConfigureAwait(false);
        await domainEvents.DispatchAsync(
            new ReleaseDeployedEvent(releaseId, pipelineRunId, stageName, IsRollback: false), ct).ConfigureAwait(false);
    }
}
