// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Answers "which server does this stage run on, and may it?" - the selector resolution (P-04 / P-01),
/// the fail-closed deploy policy, and the human-readable reasons a run shows when nothing matches.
/// </summary>
public interface IPipelineDispatchServerResolver
{
    /// <summary>True when the stage contains a native cross-agent <c>type: deploy</c> step.</summary>
    bool StageHasDeployStep(PipelineStageDefinition stageDef);

    /// <summary>
    /// Resolves the server for a stage, honouring a run-scoped local deployment target when one is set.
    /// Returns <c>null</c> when no server satisfies the stage's selectors.
    /// </summary>
    /// <param name="projectId">The run's project: an <c>environment:</c> selector is read in it (own, then
    /// shared), never in another project's homonym (neighbour of BACK-018).</param>
    Task<Server?> ResolveServerForTargetAsync(
        PipelineStageDefinition stageDef,
        IReadOnlyDictionary<string, string> resolvedVars,
        int? organizationId,
        bool deploymentStage,
        int? projectId,
        CancellationToken ct);

    /// <summary>
    /// The stage as the selectors should actually be read: a run-scoped local deployment target
    /// replaces pool/environment/agent. Callers that query candidate servers themselves need it.
    /// </summary>
    PipelineStageDefinition ResolveEffectiveStageTarget(
        PipelineStageDefinition stageDef,
        IReadOnlyDictionary<string, string> resolvedVars);

    /// <summary>The reason to show when no runner matched.</summary>
    string BuildNoServerReason(string stageName, PipelineStageDefinition stageDef);

    /// <summary>The reason to show when the parameter that names the stage's target is empty, so no
    /// target is chosen: no agent was searched, and none could be missing.</summary>
    string BuildEmptyTargetParameterReason(string stageName, string parameterName);

    /// <summary>The reason a launch is refused when the parameter that names a stage's target is empty:
    /// the stage would otherwise run on whichever runner is online first.</summary>
    string BuildEmptyTargetParameterRefusal(string stageName, string parameterName);

    /// <summary>The reason to show when no deployment-capable agent matched.</summary>
    string BuildNoDeployTargetReason(string stageName, PipelineStageDefinition stageDef);

    /// <summary>The policy violation to show when a deploy stage landed on a non-deployment target,
    /// or <c>null</c> when the pairing is allowed.</summary>
    string? CheckDeploymentPolicy(string stageName, bool stageHasDeploy, Server server);
}

/// <summary>
/// Server resolution for stage dispatch, extracted from <see cref="PipelineRunService"/>. It reads the
/// repository and returns a server or a reason; it never mutates a run, which is why it moved out whole.
/// </summary>
public sealed class PipelineDispatchServerResolver(IPipelineRunnerTargetReader repo) : IPipelineDispatchServerResolver
{
    // Human-readable fail-closed reason. Explicit selectors never broaden to another runner.
    public string BuildNoServerReason(string stageName, PipelineStageDefinition stageDef)
    {
        var target = DescribeTarget(stageDef);
        var osNote = !string.IsNullOrEmpty(stageDef.Os) ? $", os '{stageDef.Os}'" : string.Empty;
        return $"Stage '{stageName}': no online pipeline runner available in the organization (requested {target}{osNote}).";
    }

    public string BuildEmptyTargetParameterReason(string stageName, string parameterName)
        => $"Stage '{stageName}': parameter '{parameterName}' is empty, so no server is chosen for it.";

    public string BuildEmptyTargetParameterRefusal(string stageName, string parameterName)
        => $"{BuildEmptyTargetParameterReason(stageName, parameterName)} "
           + $"The launch is refused instead of running the stage on any available runner: choose a value for '{parameterName}'.";

    // Only the native cross-agent `type: deploy` operation requires the deployment module.
    // `execution_role: deploy` is a workload classification used by deployment-only runners; shell,
    // artifact and other ordinary pipeline steps still require a pipeline runner, not the unrelated
    // cross-agent deploy helper installed by `--module deployment`.
    public bool StageHasDeployStep(PipelineStageDefinition stageDef)
        => stageDef.Steps.Any(s => string.Equals(s.Type, "deploy", StringComparison.OrdinalIgnoreCase));

    public string BuildNoDeployTargetReason(string stageName, PipelineStageDefinition stageDef)
    {
        var target = DescribeTarget(stageDef);
        var osNote = !string.IsNullOrEmpty(stageDef.Os) ? $", os '{stageDef.Os}'" : string.Empty;
        return $"Deploy stage '{stageName}': no online deployment-capable agent available (requested {target}{osNote}). "
             + "Install an agent with --module deployment, or target a host that has the deployment capability.";
    }

    // Fail-closed deploy policy (mirrors CheckIsolationPolicy): a deploy stage may only run on a
    // server whose deployment capability is present. Returns a concrete reason on violation, else null.
    public string? CheckDeploymentPolicy(string stageName, bool stageHasDeploy, Server server)
        => stageHasDeploy && !server.DeploymentTargetAvailable
            ? $"Deploy stage '{stageName}': server '{server.Name}' is not a deployment target (missing the deployment capability)."
            : null;

    public async Task<Server?> ResolveServerForTargetAsync(
        PipelineStageDefinition stageDef, IReadOnlyDictionary<string, string> resolvedVars,
        int? organizationId, bool deploymentStage, int? projectId, CancellationToken ct)
    {
        var effectiveStage = ResolveEffectiveStageTarget(stageDef, resolvedVars);
        return deploymentStage
            ? await repo.FindOnlineDeployTargetAsync(
                effectiveStage.Pool, effectiveStage.Environment, effectiveStage.Agent,
                OsTypeHelper.Parse(effectiveStage.Os), organizationId, projectId, ct).ConfigureAwait(false)
            : await ResolveServerAsync(effectiveStage, organizationId, projectId, ct).ConfigureAwait(false);
    }

    // The two reason builders describe the same selector precedence; saying it once keeps them honest.
    private static string DescribeTarget(PipelineStageDefinition stageDef)
        => !string.IsNullOrEmpty(stageDef.Pool) ? $"pool '{stageDef.Pool}'"
            : !string.IsNullOrEmpty(stageDef.Environment) ? $"environment '{stageDef.Environment}'"
            : !string.IsNullOrEmpty(stageDef.Agent) ? $"agent '{stageDef.Agent}'"
            : "default";

    // P-04 / P-01: Server resolution - pool > environment > agent name. Explicit selectors are
    // fail-closed; only stages with no selector may use an organization-scoped runner fallback.
    private async Task<Server?> ResolveServerAsync(
        PipelineStageDefinition stageDef, int? organizationId, int? projectId, CancellationToken ct)
    {
        var requiredOs = OsTypeHelper.Parse(stageDef.Os);
        Server? server = null;

        if (!string.IsNullOrEmpty(stageDef.Pool))
            server = organizationId is { } poolOrg
                ? await repo.FindOnlineServerInPoolInOrganizationAsync(stageDef.Pool, requiredOs, poolOrg, ct).ConfigureAwait(false)
                : await repo.FindOnlineServerInPoolAsync(stageDef.Pool, requiredOs, ct).ConfigureAwait(false);
        else if (!string.IsNullOrEmpty(stageDef.Environment))
            server = organizationId is { } environmentOrg
                ? await repo.FindOnlineServerInEnvironmentInOrganizationAsync(stageDef.Environment, requiredOs, environmentOrg, projectId, ct).ConfigureAwait(false)
                : await repo.FindOnlineServerInEnvironmentAsync(stageDef.Environment, projectId, requiredOs, ct).ConfigureAwait(false);
        else if (!string.IsNullOrEmpty(stageDef.Agent) && !stageDef.Agent.Equals("default", StringComparison.OrdinalIgnoreCase))
            server = organizationId is { } agentOrg
                ? await repo.FindOnlineServerByAgentInOrganizationAsync(stageDef.Agent, requiredOs, agentOrg, ct).ConfigureAwait(false)
                : await repo.FindOnlineServerByAgentAsync(stageDef.Agent, requiredOs, ct).ConfigureAwait(false);

        // A globally named pool/environment/agent must never cross the pipeline's organization boundary.
        if (server is not null && organizationId is { } orgId && server.OrganizationId != orgId)
            server = null;

        var hasExplicitSelector = !string.IsNullOrEmpty(stageDef.Pool)
            || !string.IsNullOrEmpty(stageDef.Environment)
            || !string.IsNullOrEmpty(stageDef.Agent) && !stageDef.Agent.Equals("default", StringComparison.OrdinalIgnoreCase);
        if (hasExplicitSelector) return server;

        return await repo.FindAnyOnlineRunnerAsync(organizationId, requiredOs, ct).ConfigureAwait(false);
    }

    public PipelineStageDefinition ResolveEffectiveStageTarget(
        PipelineStageDefinition stageDef,
        IReadOnlyDictionary<string, string> resolvedVars)
    {
        if (resolvedVars.TryGetValue(PipelineDeploymentTargetGuard.TargetVariable, out var target)
            && target.Equals(PipelineDeploymentTargetGuard.Local, StringComparison.OrdinalIgnoreCase)
            && resolvedVars.TryGetValue(PipelineDeploymentTargetGuard.LocalAgentVariable, out var localAgent)
            && !string.IsNullOrWhiteSpace(localAgent))
        {
            return stageDef with { Pool = null, Environment = null, Agent = localAgent.Trim() };
        }

        return stageDef;
    }
}
