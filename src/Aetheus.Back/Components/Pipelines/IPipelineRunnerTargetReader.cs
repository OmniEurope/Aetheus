// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Runner and deploy-target selection for a stage: the online server a selector (agent, pool,
/// environment, id) resolves to, the full candidate set the same rules allow, and the agent facts the
/// launch preflight checks them against. The port of the dispatch resolver, the ready-stage server
/// selector and the scanner manifest preflight (audit AUD-07). <see cref="IPipelineRepository"/>
/// composes it and delegates to the same queries.
/// </summary>
public interface IPipelineRunnerTargetReader
{
    // The optional requiredOs filters candidates to a single OS family (pipeline `os:` typing).
    // OsType.Unknown means "no constraint" - the pre-typing behavior.
    Task<Server?> FindOnlineServerByAgentAsync(string agent, OsType requiredOs = OsType.Unknown, CancellationToken ct = default);
    Task<Server?> FindOnlineServerByAgentInOrganizationAsync(string agent, OsType requiredOs, int organizationId, CancellationToken ct = default);

    Task<Server?> FindOnlineServerInPoolAsync(string poolName, OsType requiredOs = OsType.Unknown, CancellationToken ct = default);
    Task<Server?> FindOnlineServerInPoolInOrganizationAsync(string poolName, OsType requiredOs, int organizationId, CancellationToken ct = default);

    /// <summary>Neighbour of BACK-018: the environment is read in <paramref name="projectId"/> (its own,
    /// then a shared one), never another project's homonym; the same for every environment selector below.</summary>
    Task<Server?> FindOnlineServerInEnvironmentAsync(string environmentName, int? projectId, OsType requiredOs = OsType.Unknown, CancellationToken ct = default);
    Task<Server?> FindOnlineServerInEnvironmentInOrganizationAsync(string environmentName, OsType requiredOs, int organizationId, int? projectId, CancellationToken ct = default);

    /// <summary>Any online pipeline-runner, scoped to <paramref name="organizationId"/> when given.
    /// Always-runnable fallback when a stage's specific selector matches nothing.</summary>
    Task<Server?> FindAnyOnlineRunnerAsync(int? organizationId, OsType requiredOs = OsType.Unknown, CancellationToken ct = default);

    /// <summary>Cross-agent deploy targeting (fail-closed): pool > environment > agent precedence over
    /// DeploymentTargetAvailable servers only, with a deploy-capable org fallback. Never a plain runner.</summary>
    Task<Server?> FindOnlineDeployTargetAsync(string? pool, string? environment, string? agent, OsType requiredOs, int? organizationId, int? projectId, CancellationToken ct = default);
    /// <summary>Returns every server that the stage resolver can select, using the same selector,
    /// organization, OS and deploy-capability rules as execution.</summary>
    Task<List<int>> FindCandidateTargetServerIdsAsync(string? pool, string? environment, string? agent, OsType requiredOs, int? organizationId, bool deploymentStage, int? projectId, CancellationToken ct = default);
    Task<Server?> FindOnlineServerByIdAsync(int serverId, OsType requiredOs = OsType.Unknown, CancellationToken ct = default);
    /// <summary>The last reported agent facts of the named servers, for the launch preflight.</summary>
    Task<List<PipelineRunnerFacts>> GetRunnerFactsAsync(IReadOnlyCollection<int> serverIds, CancellationToken ct = default);
}
