// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Server resolution methods - split from <see cref="PipelineRepository"/> to stay within the
/// 600-line file budget. Covers agent/pool/environment matching, the always-runnable org fallback,
/// and the F-EXEC-1 authorization candidate enumeration. Its runner and deploy-target selection is also
/// registered on its own as <see cref="IPipelineRunnerTargetReader"/> (audit AUD-07).
/// </summary>
internal sealed class PipelineServerResolver(AppDbContext db) : IPipelineRunnerTargetReader
{
    public async Task<Server?> FindOnlineServerByAgentAsync(string agent, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
    {
        // Item #11 secure-by-default: PipelineRunnerEnabled must be true for the scheduler to ever
        // dispatch to this server. The F-EXEC-1 authorization gate (FindServerIds*) is intentionally
        // NOT filtered - see entity comment.

        // When no specific agent is requested (empty / "default"), pick any online pipeline runner.
        if (string.IsNullOrEmpty(agent) || agent.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return await db.Servers.WhereAgentCan(AgentCapabilities.PipelineBuild)
                .Where(s => s.Status == ServerStatus.Online && s.PipelineRunnerEnabled)
                .Where(s => requiredOs == OsType.Unknown || s.OsType == requiredOs)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        // Fast path: indexed name match.
        var byName = await db.Servers.WhereAgentCan(AgentCapabilities.PipelineBuild)
            .Where(s => s.Status == ServerStatus.Online && s.PipelineRunnerEnabled && s.Name == agent)
            .Where(s => requiredOs == OsType.Unknown || s.OsType == requiredOs)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (byName is not null) return byName;

        // Tag match: load only the (small) set of online servers and do exact JSON-array
        // membership in memory. O(N_online) instead of O(N_total) substring scan.
        var candidates = await db.Servers.WhereAgentCan(AgentCapabilities.PipelineBuild)
            .Where(s => s.Status == ServerStatus.Online && s.PipelineRunnerEnabled && s.Tags != null && s.Tags != "")
            .Where(s => requiredOs == OsType.Unknown || s.OsType == requiredOs)
            .Select(s => new { s.Id, s.Tags })
            .ToListAsync(ct).ConfigureAwait(false);

        var matchId = candidates
            .FirstOrDefault(s => TagsHelper.DeserializeTags(s.Tags).Contains(agent, StringComparer.OrdinalIgnoreCase))
            ?.Id;

        return matchId is null
            ? null
            : await db.Servers.FirstOrDefaultAsync(s => s.Id == matchId.Value, ct).ConfigureAwait(false);
    }

    public async Task<Server?> FindOnlineServerByAgentInOrganizationAsync(
        string agent, OsType requiredOs, int organizationId, CancellationToken ct = default)
    {
        var candidates = await db.Servers.WhereAgentCan(AgentCapabilities.PipelineBuild)
            .Where(s => s.OrganizationId == organizationId
                        && s.Status == ServerStatus.Online
                        && s.PipelineRunnerEnabled)
            .Where(s => requiredOs == OsType.Unknown || s.OsType == requiredOs)
            .ToListAsync(ct).ConfigureAwait(false);
        return candidates.FirstOrDefault(s => s.Name == agent
            || TagsHelper.DeserializeTags(s.Tags).Contains(agent, StringComparer.OrdinalIgnoreCase));
    }

    // F-EXEC-1: every server whose name OR tag matches the selector, ANY status. Mirrors
    // FindOnlineServerByAgentAsync minus the Online filter; used only for the pre-run
    // Server.Admin authorization gate, never for scheduling.
    public async Task<List<int>> FindServerIdsByAgentAsync(string? agent, CancellationToken ct = default)
    {
        // When no specific agent is requested (empty / "default"), return all pipeline runners.
        if (string.IsNullOrEmpty(agent) || agent.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return await db.Servers
                .Where(s => s.PipelineRunnerEnabled)
                .Select(s => s.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var byName = await db.Servers
            .Where(s => s.Name == agent)
            .Select(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var tagged = await db.Servers
            .Where(s => s.Tags != null && s.Tags != "")
            .Select(s => new { s.Id, s.Tags })
            .ToListAsync(ct).ConfigureAwait(false);

        var taggedIds = tagged
            .Where(s => TagsHelper.DeserializeTags(s.Tags).Contains(agent, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Id);

        return byName.Union(taggedIds).ToList();
    }

    public async Task<Server?> FindOnlineServerInPoolAsync(string poolName, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
        => await FindOnlineServerAsync(
            db.AgentPoolServers.Where(item => item.AgentPool.Name == poolName).Select(item => item.Server),
            requiredOs,
            organizationId: null,
            ct).ConfigureAwait(false);

    public async Task<Server?> FindOnlineServerInPoolInOrganizationAsync(
        string poolName, OsType requiredOs, int organizationId, CancellationToken ct = default)
        => await FindOnlineServerAsync(
            db.AgentPoolServers.Where(item => item.AgentPool.Name == poolName).Select(item => item.Server),
            requiredOs,
            organizationId,
            ct).ConfigureAwait(false);

    public async Task<Server?> FindOnlineServerInEnvironmentAsync(
        string environmentName, int? projectId, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
        => await FindOnlineServerAsync(
            EnvironmentMembers(environmentName, projectId),
            requiredOs,
            organizationId: null,
            ct).ConfigureAwait(false);

    public async Task<Server?> FindOnlineServerInEnvironmentInOrganizationAsync(
        string environmentName, OsType requiredOs, int organizationId, int? projectId, CancellationToken ct = default)
        => await FindOnlineServerAsync(
            EnvironmentMembers(environmentName, projectId),
            requiredOs,
            organizationId,
            ct).ConfigureAwait(false);

    /// <summary>
    /// Neighbour of BACK-018: the servers of the environment a stage of <paramref name="projectId"/> means
    /// by <paramref name="environmentName"/>. A name is unique per project only, so it is the project's own
    /// environment first, then a shared one (no project), never another project's homonym, which the
    /// organization filter alone let through. The same rule the approval, check and lock gates read
    /// (<see cref="IPipelineEnvironmentReader.FindEnvironmentByNameAsync"/>); a null project sees shared ones only.
    /// </summary>
    private IQueryable<Server> EnvironmentMembers(string environmentName, int? projectId)
    {
        var environmentId = db.Environments
            .Where(environment => environment.Name == environmentName
                && (environment.ProjectId == null || environment.ProjectId == projectId))
            .OrderBy(environment => environment.ProjectId == null)
            .ThenBy(environment => environment.Id)
            .Select(environment => environment.Id)
            .Take(1);
        return db.EnvironmentServers
            .Where(item => environmentId.Contains(item.EnvironmentId))
            .Select(item => item.Server);
    }

    // Always-runnable fallback: any online pipeline-runner, scoped to the project's organization
    // when known. A project/environment pipeline must run regardless of whether a pool/environment/
    // server was specified - only a total absence of online org runners blocks it.
    public async Task<Server?> FindOnlineServerByIdAsync(int serverId, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
    {
        return await db.Servers.WhereAgentCan(AgentCapabilities.PipelineBuild)
            .Where(s => s.Id == serverId
                        && s.Status == ServerStatus.Online
                        && s.PipelineRunnerEnabled)
            .Where(s => requiredOs == OsType.Unknown || s.OsType == requiredOs)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<Server?> FindServerByIdAsync(int serverId, CancellationToken ct = default)
        => db.Servers.FirstOrDefaultAsync(server => server.Id == serverId, ct);

    public async Task<int?> GetRunAffinityServerIdAsync(int runId, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId && s.ServerId != null && s.Status == TaskExecutionStatus.Success)
            .OrderByDescending(s => s.CompletedAt)
            .Select(s => s.ServerId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int?> GetStageProducerServerIdAsync(int runId, string stageName, CancellationToken ct = default)
    {
        return await db.PipelineStepRuns
            .Where(s => s.PipelineRunId == runId
                        && s.StageName == stageName
                        && s.ServerId != null
                        && s.Status == TaskExecutionStatus.Success)
            .OrderByDescending(s => s.CompletedAt)
            .Select(s => s.ServerId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<PipelineRunnerFacts>> GetRunnerFactsAsync(
        IReadOnlyCollection<int> serverIds, CancellationToken ct = default)
    {
        if (serverIds.Count == 0) return [];
        var rows = await db.Servers.AsNoTracking()
            .Where(server => serverIds.Contains(server.Id))
            .OrderBy(server => server.Name)
            .Select(server => new
            {
                server.Id,
                server.Name,
                server.Status,
                server.AgentProtocolVersion,
                server.AgentCapabilitiesJson,
                server.ScannerCapabilitiesJson
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(row => new PipelineRunnerFacts(
                row.Id, row.Name, row.Status, row.AgentProtocolVersion,
                ServerDataMapper.DeserializeDiagnostics(row.AgentCapabilitiesJson),
                row.ScannerCapabilitiesJson))
            .ToList();
    }

    public async Task<List<int>> FindCandidateTargetServerIdsAsync(
        string? pool, string? environment, string? agent, OsType requiredOs,
        int? organizationId, bool deploymentStage, int? projectId, CancellationToken ct = default)
    {
        var hasAgentSelector = !string.IsNullOrEmpty(agent)
            && !agent.Equals("default", StringComparison.OrdinalIgnoreCase);
        var hasExplicitSelector = !string.IsNullOrEmpty(pool) || !string.IsNullOrEmpty(environment) || hasAgentSelector;
        List<int> selectedIds;
        if (!string.IsNullOrEmpty(pool))
            selectedIds = await FindPoolCandidateIdsAsync(
                pool, requiredOs, organizationId, deploymentStage, ct).ConfigureAwait(false);
        else if (!string.IsNullOrEmpty(environment))
            selectedIds = await FindCandidateIdsAsync(
                EnvironmentMembers(environment, projectId), requiredOs, organizationId, deploymentStage, ct)
                .ConfigureAwait(false);
        else if (hasAgentSelector)
            selectedIds = await FindAgentCandidateIdsAsync(
                agent!, requiredOs, organizationId, deploymentStage, ct).ConfigureAwait(false);
        else
            selectedIds = [];
        if (hasExplicitSelector) return selectedIds;
        var fallbackIds = await FindFallbackCandidateIdsAsync(
            requiredOs, organizationId, deploymentStage, ct).ConfigureAwait(false);
        return selectedIds.Union(fallbackIds).Distinct().ToList();
    }

    private async Task<List<int>> FindPoolCandidateIdsAsync(
        string pool, OsType requiredOs, int? organizationId, bool deploymentStage, CancellationToken ct)
        => await FindCandidateIdsAsync(
            db.AgentPoolServers.Where(item => item.AgentPool.Name == pool).Select(item => item.Server),
            requiredOs,
            organizationId,
            deploymentStage,
            ct).ConfigureAwait(false);

    private static async Task<Server?> FindOnlineServerAsync(
        IQueryable<Server> query,
        OsType requiredOs,
        int? organizationId,
        CancellationToken ct)
    {
        query = query.Where(server => server.Status == ServerStatus.Online && server.PipelineRunnerEnabled);
        if (organizationId is { } orgId)
            query = query.Where(server => server.OrganizationId == orgId);
        if (requiredOs != OsType.Unknown)
            query = query.Where(server => server.OsType == requiredOs);

        return await query
            .WhereAgentCan(AgentCapabilities.PipelineBuild)
            .OrderBy(server => server.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private static async Task<List<int>> FindCandidateIdsAsync(
        IQueryable<Server> query,
        OsType requiredOs,
        int? organizationId,
        bool deploymentStage,
        CancellationToken ct)
    {
        query = deploymentStage
            ? query.Where(server => server.DeploymentTargetAvailable)
            : query.Where(server => server.PipelineRunnerEnabled);
        if (organizationId is { } orgId)
            query = query.Where(server => server.OrganizationId == orgId);
        if (requiredOs != OsType.Unknown)
            query = query.Where(server => server.OsType == requiredOs);

        return await query.Select(server => server.Id).Distinct().ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<int>> FindAgentCandidateIdsAsync(
        string agent, OsType requiredOs, int? organizationId, bool deploymentStage, CancellationToken ct)
    {
        var query = db.Servers.Where(server => deploymentStage
            ? server.DeploymentTargetAvailable
            : server.PipelineRunnerEnabled);
        if (organizationId is { } orgId) query = query.Where(server => server.OrganizationId == orgId);
        if (requiredOs != OsType.Unknown) query = query.Where(server => server.OsType == requiredOs);
        var candidates = await query.Select(server => new { server.Id, server.Name, server.Tags })
            .ToListAsync(ct).ConfigureAwait(false);
        return candidates
            .Where(server => server.Name == agent
                             || TagsHelper.DeserializeTags(server.Tags).Contains(agent, StringComparer.OrdinalIgnoreCase))
            .Select(server => server.Id)
            .Distinct()
            .ToList();
    }

    private async Task<List<int>> FindFallbackCandidateIdsAsync(
        OsType requiredOs, int? organizationId, bool deploymentStage, CancellationToken ct)
    {
        var fallback = db.Servers.Where(s => deploymentStage
            ? s.DeploymentTargetAvailable
            : s.PipelineRunnerEnabled);
        if (organizationId is { } fallbackOrgId) fallback = fallback.Where(s => s.OrganizationId == fallbackOrgId);
        if (requiredOs != OsType.Unknown) fallback = fallback.Where(s => s.OsType == requiredOs);
        return await fallback.Select(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
    }

    // Cross-agent deploy targeting - FAIL-CLOSED. Mirrors the pool > environment > agent precedence of
    // ResolveServerAsync but filters on DeploymentTargetAvailable (NOT PipelineRunnerEnabled - a pure
    // deploy host need not be a CI/CD runner) and NEVER falls back to a plain pipeline runner. The
    // org-wide fallback is itself deploy-capable-only, so a deploy can never land on a non-deploy host.
    public async Task<Server?> FindOnlineDeployTargetAsync(
        string? pool, string? environment, string? agent, OsType requiredOs, int? organizationId,
        int? projectId, CancellationToken ct = default)
    {
        // An EXPLICIT selector that resolves to no deploy-capable host must FAIL (return null), never
        // fall back to "any deploy-capable host in the org" - otherwise a mistyped/unprovisioned
        // `environment: staging` would silently deploy to the prod host. The org-wide fallback is
        // reserved for the no-selector case (a single deploy host with no explicit target).
        var hasExplicitSelector = !string.IsNullOrEmpty(pool)
            || !string.IsNullOrEmpty(environment)
            || (!string.IsNullOrEmpty(agent) && !agent.Equals("default", StringComparison.OrdinalIgnoreCase));

        Server? server = null;
        if (!string.IsNullOrEmpty(pool))
            server = await db.AgentPoolServers
                .Where(aps => aps.AgentPool.Name == pool
                              && aps.Server.Status == ServerStatus.Online
                              && aps.Server.DeploymentTargetAvailable)
                .Where(aps => organizationId == null || aps.Server.OrganizationId == organizationId.Value)
                .Where(aps => requiredOs == OsType.Unknown || aps.Server.OsType == requiredOs)
                .Select(aps => aps.Server)
                .WhereAgentCan(AgentCapabilities.Deployment)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        else if (!string.IsNullOrEmpty(environment))
            server = await EnvironmentMembers(environment, projectId)
                .Where(s => s.Status == ServerStatus.Online && s.DeploymentTargetAvailable)
                .Where(s => organizationId == null || s.OrganizationId == organizationId.Value)
                .Where(s => requiredOs == OsType.Unknown || s.OsType == requiredOs)
                .WhereAgentCan(AgentCapabilities.Deployment)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        else if (!string.IsNullOrEmpty(agent) && !agent.Equals("default", StringComparison.OrdinalIgnoreCase))
            server = await db.Servers.WhereAgentCan(AgentCapabilities.Deployment)
                .Where(s => s.Status == ServerStatus.Online && s.DeploymentTargetAvailable && s.Name == agent)
                .Where(s => organizationId == null || s.OrganizationId == organizationId.Value)
                .Where(s => requiredOs == OsType.Unknown || s.OsType == requiredOs)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (server is not null) return server;

        // An explicit selector that matched nothing is a hard miss - do NOT broaden to the org.
        if (hasExplicitSelector) return null;

        // No-selector org fallback: any ONLINE DEPLOY-CAPABLE server in the org. Lets a single deploy
        // host with no explicit selector still work, without ever resolving to a non-deploy runner.
        var fallback = db.Servers
            .WhereAgentCan(AgentCapabilities.Deployment)
            .Where(s => s.Status == ServerStatus.Online && s.DeploymentTargetAvailable);
        if (organizationId is { } orgId) fallback = fallback.Where(s => s.OrganizationId == orgId);
        if (requiredOs != OsType.Unknown) fallback = fallback.Where(s => s.OsType == requiredOs);
        return await fallback
            .OrderBy(s => db.Tasks.Count(t => t.ServerId == s.Id && t.Status == TaskExecutionStatus.Running))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<Server?> FindAnyOnlineRunnerAsync(int? organizationId, OsType requiredOs = OsType.Unknown, CancellationToken ct = default)
    {
        var query = db.Servers
            .WhereAgentCan(AgentCapabilities.PipelineBuild)
            .Where(s => s.Status == ServerStatus.Online && s.PipelineRunnerEnabled);
        if (organizationId is { } orgId)
            query = query.Where(s => s.OrganizationId == orgId);
        if (requiredOs != OsType.Unknown)
            query = query.Where(s => s.OsType == requiredOs);
        return await query
            .OrderBy(s => db.Tasks.Count(t => t.ServerId == s.Id && t.Status == TaskExecutionStatus.Running))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> HasRunnerWithDockerAsync(int? organizationId, CancellationToken ct = default)
    {
        var query = db.Servers
            .WhereAgentCan(AgentCapabilities.PipelineBuild)
            .Where(s => s.PipelineRunnerEnabled && s.DockerAvailable);
        if (organizationId is { } orgId)
            query = query.Where(s => s.OrganizationId == orgId);
        return await query.AnyAsync(ct).ConfigureAwait(false);
    }

    // Resolve the organization owning a pipeline by walking its owner chain
    // (Project | Environment→Project | ProjectServer→Project). Null if unresolvable (legacy orphan).
    public async Task<int?> GetPipelineOrganizationIdAsync(int pipelineId, CancellationToken ct = default)
    {
        var owner = await db.Pipelines
            .Where(p => p.Id == pipelineId)
            .Select(p => new { p.ProjectId, p.EnvironmentId, p.ProjectServerId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (owner is null) return null;

        if (owner.ProjectId is { } projectId)
            return await db.Projects.Where(p => p.Id == projectId)
                .Select(p => (int?)p.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (owner.EnvironmentId is { } environmentId)
            return await db.Environments.Where(e => e.Id == environmentId && e.ProjectId != null)
                .Select(e => (int?)e.Project!.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (owner.ProjectServerId is { } projectServerId)
            return await db.ProjectServers.Where(ps => ps.Id == projectServerId)
                .Select(ps => (int?)ps.Project.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return null;
    }

    public async Task<int?> GetPipelineOwnerOrganizationIdAsync(
        int? projectId, int? environmentId, int? projectServerId, CancellationToken ct = default)
    {
        if (projectId is { } directProjectId)
            return await db.Projects.Where(project => project.Id == directProjectId)
                .Select(project => (int?)project.OrganizationId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (environmentId is { } directEnvironmentId)
            return await db.Environments.Where(environment => environment.Id == directEnvironmentId
                    && environment.ProjectId != null)
                .Select(environment => (int?)environment.Project!.OrganizationId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (projectServerId is { } directProjectServerId)
            return await db.ProjectServers.Where(projectServer => projectServer.Id == directProjectServerId)
                .Select(projectServer => (int?)projectServer.Project.OrganizationId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return null;
    }

    public Task<int?> GetAiRunnerProfileOrganizationIdAsync(int profileId, CancellationToken ct = default) =>
        db.AiRunnerProfiles.AsNoTracking().Where(profile => profile.Id == profileId)
            .Select(profile => (int?)profile.OrganizationId).FirstOrDefaultAsync(ct);

    public async Task<List<Pipeline>> GetPipelinesByEnvironmentAsync(int environmentId, CancellationToken ct = default)
    {
        return await db.Pipelines
            .AsNoTracking()
            .Where(p => p.EnvironmentId == environmentId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<(string Name, int ProjectId)?> GetEnvironmentCopyTargetAsync(int environmentId, CancellationToken ct = default)
    {
        var row = await db.Environments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.ProjectId != null)
            .Select(e => new { e.Name, e.ProjectId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row?.ProjectId is { } projectId ? (row.Name, projectId) : null;
    }

    public async Task<int?> GetPipelineProjectIdAsync(Pipeline pipeline, CancellationToken ct = default)
    {
        if (pipeline.ProjectId is not null) return pipeline.ProjectId;
        if (pipeline.EnvironmentId is { } envId)
            return await db.Environments.Where(e => e.Id == envId).Select(e => e.ProjectId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline.ProjectServerId is { } psId)
            return await GetProjectServerProjectIdAsync(psId, ct).ConfigureAwait(false);
        return null;
    }

    public async Task<DeploymentGateRelease?> FindDeploymentGateReleaseAsync(
        int projectId, string releaseSelector, CancellationToken ct = default)
    {
        var releases = db.Releases.AsNoTracking().Where(release => release.ProjectId == projectId);
        releases = int.TryParse(releaseSelector, out var releaseId)
            ? releases.Where(release => release.Id == releaseId)
            : releases.Where(release => release.Version == releaseSelector);
        return await releases
            .OrderByDescending(release => release.DetectedAt)
            .Select(release => new DeploymentGateRelease(
                release.Id, release.Version, release.AssuranceGrade, release.Deployable))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<BranchAdvanceRelease?> FindBranchAdvanceReleaseAsync(
        int projectId, string version, CancellationToken ct = default) =>
        await db.Releases.AsNoTracking()
            .Where(release => release.ProjectId == projectId && release.Version == version)
            .OrderByDescending(release => release.DetectedAt)
            .Select(release => new BranchAdvanceRelease(release.Id, release.Version, release.Status, release.CommitHash))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <summary>Project a project-server row belongs to. Null when the row does not exist, which the
    /// authorization callers treat as a refusal: there is no <c>ResourceType.ProjectServer</c> to check
    /// directly, so the owning project's permission is what stands in for it.</summary>
    public async Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default) =>
        await db.ProjectServers.AsNoTracking()
            .Where(projectServer => projectServer.Id == projectServerId)
            .Select(projectServer => (int?)projectServer.ProjectId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    // S-FEAT-16: the project's default release version pattern (null when unset → caller falls back).
    public async Task<string?> GetProjectReleasePatternAsync(int projectId, CancellationToken ct = default) =>
        await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.ReleaseNumberingPattern)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task<string?> GetProjectNameAsync(int projectId, CancellationToken ct = default) =>
        await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    // F-EXEC-1: all pool members, ANY status (authorization gate only).
    public async Task<List<int>> FindServerIdsInPoolAsync(string poolName, CancellationToken ct = default)
    {
        return await db.AgentPoolServers
            .Where(aps => aps.AgentPool.Name == poolName)
            .Select(aps => aps.ServerId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // F-EXEC-1: all environment members, ANY status (authorization gate only).
    public async Task<List<int>> FindServerIdsInEnvironmentAsync(
        string environmentName, int? projectId, CancellationToken ct = default)
    {
        return await EnvironmentMembers(environmentName, projectId)
            .Select(server => server.Id)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
