// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Which runner a ready stage goes to, and what it means when none is available.
///
/// The distinction this class exists to make is the one the run page depends on: a stage with no
/// online runner but a configured one is WAITING, and a stage with no configured target at all is
/// UNMATCHED and fails the run. Confusing the two either kills a run that would have recovered on
/// its own, or leaves one alive forever on a target nobody ever declared.
///
/// Extracted from <see cref="PipelineStageDispatchPlanner"/>, which crossed the file-size budget
/// once it started recording why it was waiting (FileSizeAuditTests).
/// </summary>
/// <param name="Server">The runner to dispatch to, or null when none was selected.</param>
/// <param name="RunnerTemporarilyUnavailable">A target IS configured but none is online: wait.</param>
/// <param name="Reason">Why no runner can ever match this stage as written: fail.</param>
internal sealed record ReadyStageServerResolution(
    Server? Server,
    bool RunnerTemporarilyUnavailable,
    string? Reason);

internal sealed class PipelineReadyStageServerSelector(
    IPipelineRunnerTargetReader repo,
    IPipelineDispatchServerResolver dispatchServers,
    ILogger logger)
{
    public async Task<ReadyStageServerResolution> ResolveAsync(
        int runId,
        string stageName,
        PipelineStageDefinition stageDef,
        Dictionary<string, string> resolvedVars,
        int? organizationId,
        bool stageHasDeploy,
        int? affinityServerId,
        int? projectId,
        CancellationToken ct)
    {
        var effectiveStage = dispatchServers.ResolveEffectiveStageTarget(stageDef, resolvedVars);
        Server? server = null;
        List<int>? configuredTargets = null;
        if (!stageHasDeploy && affinityServerId is not null)
        {
            configuredTargets = await repo.FindCandidateTargetServerIdsAsync(
                effectiveStage.Pool, effectiveStage.Environment, effectiveStage.Agent,
                OsTypeHelper.Parse(effectiveStage.Os), organizationId, false, projectId, ct).ConfigureAwait(false);
            if (configuredTargets.Contains(affinityServerId.Value))
            {
                server = await repo.FindOnlineServerByIdAsync(
                    affinityServerId.Value, OsTypeHelper.Parse(effectiveStage.Os), ct).ConfigureAwait(false);
                if (server is null)
                    return new(null, true, null);
            }
        }
        server ??= await dispatchServers.ResolveServerForTargetAsync(
            stageDef, resolvedVars, organizationId, stageHasDeploy, projectId, ct).ConfigureAwait(false);
        if (server is not null)
            return new(server, false, null);

        configuredTargets ??= await repo.FindCandidateTargetServerIdsAsync(
            effectiveStage.Pool, effectiveStage.Environment, effectiveStage.Agent,
            OsTypeHelper.Parse(effectiveStage.Os), organizationId, stageHasDeploy, projectId, ct).ConfigureAwait(false);
        if (configuredTargets.Count > 0)
        {
            logger.LogWarning(
                "Run {RunId} stage {StageName} is waiting for a configured {TargetKind} to come back online",
                runId, stageName, stageHasDeploy ? "deployment target" : "pipeline runner");
            return new(null, true, null);
        }
        var reason = stageHasDeploy
            ? dispatchServers.BuildNoDeployTargetReason(stageName, stageDef)
            : dispatchServers.BuildNoServerReason(stageName, stageDef);
        return new(null, false, reason);
    }
}
