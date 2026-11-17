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
    /// <summary>
    /// What one dispatch pass has already asked the database about runners, so the ready stages of the same pass
    /// (parallel stages with the same selector, the usual case) do not ask again. One instance per pass, never shared
    /// between passes: nothing here outlives the locked pass that created it.
    ///
    /// Only answers about CONFIGURATION are kept: whether the run's pipeline is an external import, and which servers
    /// a selector allows. An online server found by id is kept only when it was found (a miss is asked again, so a
    /// runner that has just come back is never hidden by an earlier miss); the entity is a tracked one, so a second
    /// read would hand back the very same instance anyway. Whether a runner is still online is not re-decided for
    /// the later stages of the pass: the tasks of every stage of one pass are inserted in the same save, a few
    /// milliseconds after the first stage's check, which is the window that check always had.
    /// </summary>
    internal sealed class Pass
    {
        internal bool? ExternalImport { get; set; }

        internal Dictionary<(string? Pool, string? Environment, string? Agent, OsType Os, int? OrganizationId, bool Deployment, int? ProjectId), List<int>> Candidates { get; } = [];

        internal Dictionary<(int ServerId, OsType Os), Server> OnlineServers { get; } = [];
    }

    public async Task<ReadyStageServerResolution> ResolveAsync(
        int runId,
        string stageName,
        PipelineStageDefinition stageDef,
        Dictionary<string, string> resolvedVars,
        int? organizationId,
        bool stageHasDeploy,
        int? affinityServerId,
        int? projectId,
        CancellationToken ct,
        Pass? pass = null)
    {
        pass ??= new Pass();
        pass.ExternalImport ??= await repo.IsExternalImportRunAsync(runId, ct).ConfigureAwait(false);
        if (pass.ExternalImport.Value)
            return await ResolveForImportedPipelineAsync(
                runId, stageName, stageDef, resolvedVars, organizationId, stageHasDeploy, affinityServerId, projectId, pass, ct)
                .ConfigureAwait(false);
        var effectiveStage = dispatchServers.ResolveEffectiveStageTarget(stageDef, resolvedVars);
        Server? server = null;
        List<int>? configuredTargets = null;
        if (!stageHasDeploy && affinityServerId is not null)
        {
            configuredTargets = await CandidatesAsync(
                effectiveStage, organizationId, false, projectId, pass, ct).ConfigureAwait(false);
            if (configuredTargets.Contains(affinityServerId.Value))
            {
                server = await OnlineServerAsync(
                    affinityServerId.Value, OsTypeHelper.Parse(effectiveStage.Os), pass, ct).ConfigureAwait(false);
                if (server is null)
                    return new(null, true, null);
            }
        }
        server ??= await dispatchServers.ResolveServerForTargetAsync(
            stageDef, resolvedVars, organizationId, stageHasDeploy, projectId, ct).ConfigureAwait(false);
        if (server is not null)
            return new(server, false, null);

        configuredTargets ??= await CandidatesAsync(
            effectiveStage, organizationId, stageHasDeploy, projectId, pass, ct).ConfigureAwait(false);
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

    /// <summary>
    /// R-11 (decision of 2026-11-10): a pipeline imported from an external repository mirror runs only on the servers
    /// linked to its project. The run's affinity runner is kept only when it is one of them; a stage waits while a
    /// linked candidate is offline and fails when the project has none.
    /// </summary>
    private async Task<ReadyStageServerResolution> ResolveForImportedPipelineAsync(
        int runId, string stageName, PipelineStageDefinition stageDef, Dictionary<string, string> resolvedVars,
        int? organizationId, bool stageHasDeploy, int? affinityServerId, int? projectId, Pass pass, CancellationToken ct)
    {
        if (projectId is not { } importProjectId)
            return new(null, false,
                $"Stage '{stageName}': an imported external pipeline runs only on the servers linked to its project, and this run has no project.");
        var linked = await repo.GetProjectLinkedServerIdsAsync(importProjectId, ct).ConfigureAwait(false);
        var effectiveStage = dispatchServers.ResolveEffectiveStageTarget(stageDef, resolvedVars);
        if (!stageHasDeploy && affinityServerId is { } affinity && linked.Contains(affinity))
        {
            var affinityServer = await OnlineServerAsync(affinity, OsTypeHelper.Parse(effectiveStage.Os), pass, ct)
                .ConfigureAwait(false);
            return affinityServer is null ? new(null, true, null) : new(affinityServer, false, null);
        }
        var server = await dispatchServers.ResolveProjectServerForTargetAsync(
            stageDef, resolvedVars, organizationId, stageHasDeploy, importProjectId, ct).ConfigureAwait(false);
        if (server is not null)
            return new(server, false, null);

        var configuredTargets = await CandidatesAsync(
            effectiveStage, organizationId, stageHasDeploy, importProjectId, pass, ct).ConfigureAwait(false);
        if (configuredTargets.Any(linked.Contains))
        {
            logger.LogWarning(
                "Run {RunId} stage {StageName} is waiting for a pipeline runner linked to project {ProjectId} to come back online",
                runId, stageName, importProjectId);
            return new(null, true, null);
        }
        return new(null, false,
            $"Stage '{stageName}': an imported external pipeline runs only on the servers linked to its project, "
            + "and no pipeline runner linked to the project matches it. Link a runner server to the project.");
    }

    /// <summary>The servers the stage's selector allows, asked once per pass and selector.</summary>
    private async Task<List<int>> CandidatesAsync(
        PipelineStageDefinition effectiveStage, int? organizationId, bool deploymentStage, int? projectId,
        Pass pass, CancellationToken ct)
    {
        var os = OsTypeHelper.Parse(effectiveStage.Os);
        var key = (effectiveStage.Pool, effectiveStage.Environment, effectiveStage.Agent, os, organizationId, deploymentStage, projectId);
        if (pass.Candidates.TryGetValue(key, out var known)) return known;
        var found = await repo.FindCandidateTargetServerIdsAsync(
            effectiveStage.Pool, effectiveStage.Environment, effectiveStage.Agent,
            os, organizationId, deploymentStage, projectId, ct).ConfigureAwait(false);
        pass.Candidates[key] = found;
        return found;
    }

    /// <summary>The online runner with this id, kept for the rest of the pass once found (a miss is asked again).</summary>
    private async Task<Server?> OnlineServerAsync(int serverId, OsType os, Pass pass, CancellationToken ct)
    {
        if (pass.OnlineServers.TryGetValue((serverId, os), out var known)) return known;
        var found = await repo.FindOnlineServerByIdAsync(serverId, os, ct).ConfigureAwait(false);
        if (found is not null) pass.OnlineServers[(serverId, os)] = found;
        return found;
    }
}
