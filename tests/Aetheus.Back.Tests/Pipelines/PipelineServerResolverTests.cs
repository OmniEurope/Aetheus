// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// How a pipeline step picks its host: the agent/pool/environment selectors, the capability and
/// runner gates every scheduling read applies, and the deliberately ungated authorization
/// enumeration. The deploy resolver's central rule is also pinned here: an explicit selector that
/// matches nothing must fail rather than silently fall back to any deploy host in the organization.
/// </summary>
public sealed class PipelineServerResolverTests : IDisposable
{
    private static readonly DateTime Origin = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db;
    private readonly PipelineServerResolver _resolver;
    private readonly PipelineProjectOwnerRepository _projectOwners;

    public PipelineServerResolverTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options);
        _resolver = new PipelineServerResolver(_db);
        _projectOwners = new PipelineProjectOwnerRepository(_db);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SaveAsync() => await _db.SaveChangesAsync(Ct);

    /// <summary>
    /// A server the scheduler is allowed to pick: reachable protocol, not reserved for an update, and
    /// advertising the capability the caller asks for.
    /// </summary>
    private Server AddServer(
        int id,
        string name,
        bool runner = true,
        bool deployTarget = false,
        ServerStatus status = ServerStatus.Online,
        // The model requires Tags, so an untagged host still carries an empty JSON array.
        string tags = "[]",
        int organizationId = 7,
        OsType osType = OsType.Linux,
        bool updateReserved = false,
        params string[] capabilities)
    {
        var advertised = capabilities.Length > 0
            ? capabilities
            : [AgentCapabilities.PipelineBuild, AgentCapabilities.Deployment];
        var server = new Server
        {
            Id = id,
            Name = name,
            Hostname = name,
            OrganizationId = organizationId,
            Status = status,
            OsType = osType,
            PipelineRunnerEnabled = runner,
            DeploymentTargetAvailable = deployTarget,
            Tags = tags,
            AgentUpdateReserved = updateReserved,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[" + string.Join(",", advertised.Select(item => $"\"{item}\"")) + "]"
        };
        _db.Servers.Add(server);
        return server;
    }

    private void AddPool(int poolId, string name, params int[] serverIds)
    {
        _db.AgentPools.Add(new AgentPool { Id = poolId, Name = name });
        foreach (var serverId in serverIds)
            _db.AgentPoolServers.Add(new AgentPoolServer { AgentPoolId = poolId, ServerId = serverId });
    }

    private void AddEnvironment(int environmentId, string name, int? projectId, params int[] serverIds)
    {
        _db.Environments.Add(new Environment { Id = environmentId, Name = name, ProjectId = projectId });
        foreach (var serverId in serverIds)
            _db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = environmentId, ServerId = serverId });
    }

    // ---------- agent selector ----------

    [Theory]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public async Task FindOnlineServerByAgentAsync_WithoutASelectorTakesAnyOnlineRunner(string agent)
    {
        AddServer(1, "runner");
        await SaveAsync();

        Assert.Equal("runner", (await _resolver.FindOnlineServerByAgentAsync(agent, ct: Ct))!.Name);
    }

    [Fact]
    public async Task FindOnlineServerByAgentAsync_MatchesTheServerName()
    {
        AddServer(1, "alpha");
        AddServer(2, "beta");
        await SaveAsync();

        Assert.Equal(2, (await _resolver.FindOnlineServerByAgentAsync("beta", ct: Ct))!.Id);
    }

    [Fact]
    public async Task FindOnlineServerByAgentAsync_FallsBackToAnExactTagMatch()
    {
        AddServer(1, "alpha", tags: "[\"linux\",\"GPU\"]");
        AddServer(2, "beta", tags: "[\"windows\"]");
        await SaveAsync();

        Assert.Equal(1, (await _resolver.FindOnlineServerByAgentAsync("gpu", ct: Ct))!.Id);
        Assert.Null(await _resolver.FindOnlineServerByAgentAsync("arm", ct: Ct));
    }

    [Fact]
    public async Task FindOnlineServerByAgentAsync_HonoursTheRequiredOperatingSystem()
    {
        AddServer(1, "alpha", osType: OsType.Windows);
        await SaveAsync();

        Assert.Null(await _resolver.FindOnlineServerByAgentAsync("alpha", OsType.Linux, Ct));
        Assert.NotNull(await _resolver.FindOnlineServerByAgentAsync("alpha", OsType.Windows, Ct));
    }

    [Theory]
    [InlineData(false, ServerStatus.Online, false)]
    [InlineData(true, ServerStatus.Offline, false)]
    [InlineData(true, ServerStatus.Online, true)]
    public async Task FindOnlineServerByAgentAsync_RefusesAHostTheSchedulerMayNotUse(
        bool runner, ServerStatus status, bool updateReserved)
    {
        AddServer(1, "alpha", runner: runner, status: status, updateReserved: updateReserved);
        await SaveAsync();

        Assert.Null(await _resolver.FindOnlineServerByAgentAsync("alpha", ct: Ct));
        Assert.Null(await _resolver.FindOnlineServerByAgentAsync("", ct: Ct));
    }

    [Fact]
    public async Task FindOnlineServerByAgentAsync_RefusesAHostThatDoesNotAdvertiseTheBuildCapability()
    {
        AddServer(1, "alpha", capabilities: AgentCapabilities.Deployment);
        await SaveAsync();

        Assert.Null(await _resolver.FindOnlineServerByAgentAsync("alpha", ct: Ct));
    }

    [Fact]
    public async Task FindOnlineServerByAgentInOrganizationAsync_StaysInsideTheOrganization()
    {
        AddServer(1, "shared", organizationId: 7, tags: "[\"gpu\"]");
        AddServer(2, "shared-other", organizationId: 8, tags: "[\"gpu\"]");
        await SaveAsync();

        Assert.Equal(1, (await _resolver.FindOnlineServerByAgentInOrganizationAsync("gpu", OsType.Unknown, 7, Ct))!.Id);
        Assert.Null(await _resolver.FindOnlineServerByAgentInOrganizationAsync("gpu", OsType.Unknown, 9, Ct));
    }

    // ---------- authorization enumeration (deliberately ungated) ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    public async Task FindServerIdsByAgentAsync_WithoutASelectorListsEveryRunnerWhateverItsState(string? agent)
    {
        AddServer(1, "online-runner");
        AddServer(2, "offline-runner", status: ServerStatus.Offline);
        AddServer(3, "not-a-runner", runner: false);
        await SaveAsync();

        Assert.Equal([1, 2], (await _resolver.FindServerIdsByAgentAsync(agent, Ct)).Order());
    }

    [Fact]
    public async Task FindServerIdsByAgentAsync_UnionsTheNameAndTagMatchesWhateverTheirState()
    {
        AddServer(1, "gpu", status: ServerStatus.Offline);
        AddServer(2, "beta", tags: "[\"gpu\"]", status: ServerStatus.Offline);
        AddServer(3, "gamma", tags: "[\"cpu\"]");
        await SaveAsync();

        Assert.Equal([1, 2], (await _resolver.FindServerIdsByAgentAsync("gpu", Ct)).Order());
        Assert.Empty(await _resolver.FindServerIdsByAgentAsync("absent", Ct));
    }

    // ---------- pool and environment selectors ----------

    [Fact]
    public async Task FindOnlineServerInPoolAsync_PicksAMemberAndIgnoresOtherPools()
    {
        AddServer(1, "in-pool");
        AddServer(2, "elsewhere");
        AddPool(100, "builders", 1);
        AddPool(101, "others", 2);
        await SaveAsync();

        Assert.Equal(1, (await _resolver.FindOnlineServerInPoolAsync("builders", ct: Ct))!.Id);
        Assert.Null(await _resolver.FindOnlineServerInPoolAsync("absent", ct: Ct));
    }

    [Fact]
    public async Task FindOnlineServerInPoolInOrganizationAsync_StaysInsideTheOrganization()
    {
        AddServer(1, "ours", organizationId: 7);
        AddServer(2, "theirs", organizationId: 8);
        AddPool(100, "builders", 1, 2);
        await SaveAsync();

        Assert.Equal(2, (await _resolver.FindOnlineServerInPoolInOrganizationAsync("builders", OsType.Unknown, 8, Ct))!.Id);
        Assert.Null(await _resolver.FindOnlineServerInPoolInOrganizationAsync("builders", OsType.Unknown, 9, Ct));
    }

    [Fact]
    public async Task FindOnlineServerInEnvironmentAsync_PicksAMemberAndIgnoresOtherEnvironments()
    {
        AddServer(1, "staging-host");
        AddServer(2, "prod-host");
        AddEnvironment(200, "staging", projectId: 10, serverIds: 1);
        AddEnvironment(201, "production", projectId: 10, serverIds: 2);
        await SaveAsync();

        Assert.Equal(1, (await _resolver.FindOnlineServerInEnvironmentAsync("staging", projectId: 10, ct: Ct))!.Id);
        // Neighbour of BACK-018: another project does not reach project 10's staging.
        Assert.Null(await _resolver.FindOnlineServerInEnvironmentAsync("staging", projectId: 11, ct: Ct));
        Assert.Null(await _resolver.FindOnlineServerInEnvironmentAsync("absent", projectId: null, ct: Ct));
    }

    [Fact]
    public async Task FindOnlineServerInEnvironmentInOrganizationAsync_StaysInsideTheOrganization()
    {
        AddServer(1, "ours", organizationId: 7);
        AddServer(2, "theirs", organizationId: 8);
        AddEnvironment(200, "staging", projectId: 10, serverIds: [1, 2]);
        await SaveAsync();

        Assert.Equal(2, (await _resolver.FindOnlineServerInEnvironmentInOrganizationAsync(
            "staging", OsType.Unknown, 8, 10, Ct))!.Id);
    }

    [Fact]
    public async Task FindServerIdsInPoolAsync_AndInEnvironmentAsync_ListMembersWhateverTheirState()
    {
        AddServer(1, "offline-member", status: ServerStatus.Offline, runner: false);
        AddPool(100, "builders", 1);
        AddEnvironment(200, "staging", projectId: 10, serverIds: 1);
        await SaveAsync();

        Assert.Equal([1], await _resolver.FindServerIdsInPoolAsync("builders", Ct));
        Assert.Equal([1], await _resolver.FindServerIdsInEnvironmentAsync("staging", 10, Ct));
        Assert.Empty(await _resolver.FindServerIdsInPoolAsync("absent", Ct));
        Assert.Empty(await _resolver.FindServerIdsInEnvironmentAsync("absent", null, Ct));
    }

    // ---------- direct id and affinity ----------

    [Fact]
    public async Task FindOnlineServerByIdAsync_AppliesTheSchedulingGatesThatFindServerByIdSkips()
    {
        AddServer(1, "offline", status: ServerStatus.Offline);
        await SaveAsync();

        Assert.Null(await _resolver.FindOnlineServerByIdAsync(1, ct: Ct));
        Assert.Equal("offline", (await _resolver.FindServerByIdAsync(1, Ct))!.Name);
        Assert.Null(await _resolver.FindServerByIdAsync(404, Ct));
    }

    [Fact]
    public async Task GetRunAffinityServerIdAsync_TakesTheServerOfTheLastSuccessfulStep()
    {
        _db.PipelineStepRuns.AddRange(
            Step(1, runId: 5, serverId: 10, TaskExecutionStatus.Success, Origin),
            Step(2, runId: 5, serverId: 11, TaskExecutionStatus.Success, Origin.AddMinutes(5)),
            Step(3, runId: 5, serverId: 12, TaskExecutionStatus.Failed, Origin.AddMinutes(10)),
            Step(4, runId: 5, serverId: null, TaskExecutionStatus.Success, Origin.AddMinutes(15)),
            Step(5, runId: 6, serverId: 13, TaskExecutionStatus.Success, Origin.AddMinutes(20)));
        await SaveAsync();

        Assert.Equal(11, await _resolver.GetRunAffinityServerIdAsync(5, Ct));
        Assert.Null(await _resolver.GetRunAffinityServerIdAsync(7, Ct));
    }

    [Fact]
    public async Task GetStageProducerServerIdAsync_NarrowsTheAffinityToOneStage()
    {
        _db.PipelineStepRuns.AddRange(
            Step(1, runId: 5, serverId: 10, TaskExecutionStatus.Success, Origin, stage: "build"),
            Step(2, runId: 5, serverId: 11, TaskExecutionStatus.Success, Origin.AddMinutes(5), stage: "test"));
        await SaveAsync();

        Assert.Equal(10, await _resolver.GetStageProducerServerIdAsync(5, "build", Ct));
        Assert.Null(await _resolver.GetStageProducerServerIdAsync(5, "deploy", Ct));
    }

    // ---------- deploy target ----------

    [Fact]
    public async Task FindOnlineDeployTargetAsync_WithoutASelectorFallsBackToAnyDeployHostOfTheOrganization()
    {
        AddServer(1, "deploy-host", deployTarget: true);
        AddServer(2, "runner-only", deployTarget: false);
        await SaveAsync();

        var server = await _resolver.FindOnlineDeployTargetAsync(
            null, null, null, OsType.Unknown, 7, null, Ct);

        Assert.Equal(1, server!.Id);
    }

    [Fact]
    public async Task FindOnlineDeployTargetAsync_PrefersTheLeastBusyHostInTheFallback()
    {
        AddServer(1, "busy", deployTarget: true);
        AddServer(2, "idle", deployTarget: true);
        _db.Tasks.Add(new ServerTask { Id = 1, ServerId = 1, Status = TaskExecutionStatus.Running });
        await SaveAsync();

        var server = await _resolver.FindOnlineDeployTargetAsync(
            null, null, "default", OsType.Unknown, null, null, Ct);

        Assert.Equal(2, server!.Id);
    }

    [Fact]
    public async Task FindOnlineDeployTargetAsync_ResolvesAnExplicitPoolEnvironmentAndAgent()
    {
        AddServer(1, "pool-host", deployTarget: true);
        AddServer(2, "env-host", deployTarget: true);
        AddServer(3, "named-host", deployTarget: true);
        AddPool(100, "deployers", 1);
        AddEnvironment(200, "staging", projectId: 10, serverIds: 2);
        await SaveAsync();

        Assert.Equal(1, (await _resolver.FindOnlineDeployTargetAsync(
            "deployers", null, null, OsType.Unknown, null, null, Ct))!.Id);
        Assert.Equal(2, (await _resolver.FindOnlineDeployTargetAsync(
            null, "staging", null, OsType.Unknown, null, 10, Ct))!.Id);
        Assert.Equal(3, (await _resolver.FindOnlineDeployTargetAsync(
            null, null, "named-host", OsType.Unknown, null, null, Ct))!.Id);
    }

    [Theory]
    [InlineData("absent-pool", null, null)]
    [InlineData(null, "absent-environment", null)]
    [InlineData(null, null, "absent-host")]
    public async Task FindOnlineDeployTargetAsync_FailsRatherThanBroadenAnUnmatchedExplicitSelector(
        string? pool, string? environment, string? agent)
    {
        AddServer(1, "would-have-been-picked", deployTarget: true);
        await SaveAsync();

        Assert.Null(await _resolver.FindOnlineDeployTargetAsync(
            pool, environment, agent, OsType.Unknown, null, null, Ct));
    }

    [Fact]
    public async Task FindOnlineDeployTargetAsync_NeverResolvesToAHostThatIsNotADeployTarget()
    {
        AddServer(1, "runner-only", deployTarget: false);
        AddServer(2, "no-deploy-capability", deployTarget: true, capabilities: AgentCapabilities.PipelineBuild);
        await SaveAsync();

        Assert.Null(await _resolver.FindOnlineDeployTargetAsync(
            null, null, null, OsType.Unknown, null, null, Ct));
    }

    [Fact]
    public async Task FindAnyOnlineRunnerAsync_PrefersTheLeastBusyRunnerOfTheOrganization()
    {
        AddServer(1, "busy", organizationId: 7);
        AddServer(2, "idle", organizationId: 7);
        AddServer(3, "other-org", organizationId: 8);
        _db.Tasks.Add(new ServerTask { Id = 1, ServerId = 1, Status = TaskExecutionStatus.Running });
        await SaveAsync();

        Assert.Equal(2, (await _resolver.FindAnyOnlineRunnerAsync(7, ct: Ct))!.Id);
        Assert.Null(await _resolver.FindAnyOnlineRunnerAsync(9, ct: Ct));
        Assert.Null(await _resolver.FindAnyOnlineRunnerAsync(7, OsType.Windows, Ct));
    }

    // ---------- ownership walks ----------

    [Fact]
    public async Task GetPipelineOrganizationIdAsync_WalksEachOwnerChain()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Environments.Add(new Environment { Id = 20, Name = "staging", ProjectId = 10 });
        _db.ProjectServers.Add(new ProjectServer { Id = 30, ProjectId = 10, DisplayName = "vps-1" });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "by-project", ProjectId = 10 },
            new Pipeline { Id = 2, Name = "by-environment", EnvironmentId = 20 },
            new Pipeline { Id = 3, Name = "by-project-server", ProjectServerId = 30 },
            new Pipeline { Id = 4, Name = "orphan" });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(7, await _resolver.GetPipelineOrganizationIdAsync(1, Ct));
        Assert.Equal(7, await _resolver.GetPipelineOrganizationIdAsync(2, Ct));
        Assert.Equal(7, await _resolver.GetPipelineOrganizationIdAsync(3, Ct));
        Assert.Null(await _resolver.GetPipelineOrganizationIdAsync(4, Ct));
        Assert.Null(await _resolver.GetPipelineOrganizationIdAsync(404, Ct));
    }

    [Fact]
    public async Task GetPipelineOwnerOrganizationIdAsync_WalksTheSameChainFromRawIds()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Environments.Add(new Environment { Id = 20, Name = "staging", ProjectId = 10 });
        _db.ProjectServers.Add(new ProjectServer { Id = 30, ProjectId = 10, DisplayName = "vps-1" });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(7, await _resolver.GetPipelineOwnerOrganizationIdAsync(10, null, null, Ct));
        Assert.Equal(7, await _resolver.GetPipelineOwnerOrganizationIdAsync(null, 20, null, Ct));
        Assert.Equal(7, await _resolver.GetPipelineOwnerOrganizationIdAsync(null, null, 30, Ct));
        Assert.Null(await _resolver.GetPipelineOwnerOrganizationIdAsync(null, null, null, Ct));
    }

    [Fact]
    public async Task GetAiRunnerProfileOrganizationIdAsync_ReturnsTheOwningOrganization_OrNullWhenMissing()
    {
        _db.AiRunnerProfiles.Add(new AiRunnerProfile { Id = 5, OrganizationId = 7, Name = "Claude" });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(7, await _resolver.GetAiRunnerProfileOrganizationIdAsync(5, Ct));
        Assert.Null(await _resolver.GetAiRunnerProfileOrganizationIdAsync(404, Ct));
    }

    [Fact]
    public async Task GetPipelineProjectIdAsync_ResolvesTheProjectDirectlyOrThroughTheOwner()
    {
        _db.Environments.Add(new Environment { Id = 20, Name = "staging", ProjectId = 10 });
        _db.ProjectServers.Add(new ProjectServer { Id = 30, ProjectId = 11, DisplayName = "vps-1" });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(9, await _resolver.GetPipelineProjectIdAsync(new Pipeline { Id = 1, ProjectId = 9 }, Ct));
        Assert.Equal(10, await _resolver.GetPipelineProjectIdAsync(new Pipeline { Id = 2, EnvironmentId = 20 }, Ct));
        Assert.Equal(11, await _resolver.GetPipelineProjectIdAsync(new Pipeline { Id = 3, ProjectServerId = 30 }, Ct));
        Assert.Null(await _resolver.GetPipelineProjectIdAsync(new Pipeline { Id = 4 }, Ct));
    }

    [Fact]
    public async Task GetPipelinesByEnvironmentAsync_ReturnsOnlyThatEnvironmentsPipelines()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "staging-a", EnvironmentId = 20 },
            new Pipeline { Id = 2, Name = "staging-b", EnvironmentId = 20 },
            new Pipeline { Id = 3, Name = "prod", EnvironmentId = 21 });
        await SaveAsync();

        Assert.Equal(
            ["staging-a", "staging-b"],
            (await _resolver.GetPipelinesByEnvironmentAsync(20, Ct)).Select(pipeline => pipeline.Name).Order());
    }

    [Fact]
    public async Task GetEnvironmentCopyTargetAsync_NeedsAnEnvironmentThatBelongsToAProject()
    {
        _db.Environments.AddRange(
            new Environment { Id = 20, Name = "staging", ProjectId = 10 },
            new Environment { Id = 21, Name = "orphan" });
        await SaveAsync();

        Assert.Equal(("staging", 10), await _resolver.GetEnvironmentCopyTargetAsync(20, Ct));
        Assert.Null(await _resolver.GetEnvironmentCopyTargetAsync(21, Ct));
        Assert.Null(await _resolver.GetEnvironmentCopyTargetAsync(404, Ct));
    }

    [Fact]
    public async Task GetProjectReleasePatternAsync_ReadsThePatternOrNothing()
    {
        _db.Projects.Add(new Project
        {
            Id = 10,
            Name = "aetheus",
            OrganizationId = 7,
            ReleaseNumberingPattern = "v{major}.{minor}.{build}"
        });
        await SaveAsync();

        Assert.Equal("v{major}.{minor}.{build}", await _resolver.GetProjectReleasePatternAsync(10, Ct));
        Assert.Null(await _resolver.GetProjectReleasePatternAsync(404, Ct));
    }

    [Fact]
    public async Task GetProjectNameAsync_ReadsTheNameOrNothing()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "Mon Aetheus", OrganizationId = 7 });
        await SaveAsync();

        Assert.Equal("Mon Aetheus", await _resolver.GetProjectNameAsync(10, Ct));
        Assert.Null(await _resolver.GetProjectNameAsync(404, Ct));
    }

    [Fact]
    public async Task GetProjectOwnerUsernameAsync_ResolvesTheLowestOwnerOfTheOwningOrganization()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Users.AddRange(
            new User { Id = 1, Username = "second-owner", PasswordHash = "x" },
            new User { Id = 2, Username = "member", PasswordHash = "x" },
            new User { Id = 3, Username = "third-owner", PasswordHash = "x" });
        _db.OrganizationMembers.AddRange(
            new OrganizationMember { OrganizationId = 7, UserId = 3, Role = OrganizationRole.Owner },
            new OrganizationMember { OrganizationId = 7, UserId = 1, Role = OrganizationRole.Owner },
            new OrganizationMember { OrganizationId = 7, UserId = 2, Role = OrganizationRole.Member });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal("second-owner", await _projectOwners.GetProjectOwnerUsernameAsync(10, Ct));
        Assert.Null(await _projectOwners.GetProjectOwnerUsernameAsync(404, Ct));
    }

    [Fact]
    public async Task GetProjectOwnerUsernameAsync_ReturnsNothingWhenTheOrganizationHasNoOwner()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Users.Add(new User { Id = 1, Username = "member", PasswordHash = "x" });
        _db.OrganizationMembers.Add(
            new OrganizationMember { OrganizationId = 7, UserId = 1, Role = OrganizationRole.Member });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.Null(await _projectOwners.GetProjectOwnerUsernameAsync(10, Ct));
    }

    [Fact]
    public async Task IsActiveUsernameAsync_AcceptsOnlyALiveUser()
    {
        _db.Users.AddRange(
            new User { Id = 1, Username = "alive", PasswordHash = "x", IsActive = true },
            new User { Id = 2, Username = "disabled", PasswordHash = "x", IsActive = false });
        await SaveAsync();

        Assert.True(await _projectOwners.IsActiveUsernameAsync("alive", Ct));
        Assert.False(await _projectOwners.IsActiveUsernameAsync("disabled", Ct));
        Assert.False(await _projectOwners.IsActiveUsernameAsync("absent", Ct));
        Assert.False(await _projectOwners.IsActiveUsernameAsync("   ", Ct));
    }

    private static PipelineStepRun Step(
        int id, int runId, int? serverId, TaskExecutionStatus status, DateTime completedAt,
        string stage = "build") =>
        new()
        {
            Id = id,
            PipelineRunId = runId,
            ServerId = serverId,
            Status = status,
            CompletedAt = completedAt,
            StageName = stage,
            StepName = "step"
        };

    public void Dispose() => _db.Dispose();
}
