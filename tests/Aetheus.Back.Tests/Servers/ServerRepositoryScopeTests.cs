// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Servers;

/// <summary>
/// What a server is connected to, as the repository computes it: the union of the projects that
/// merely link the server and the projects that actually ran a pipeline step on it, plus the
/// pipelines, libraries and vaults that follow from either link. Also covers the token-bearing load,
/// the stale-offline compare-and-set and the paged project listing.
///
/// The PostgreSQL-only paths (the ExecuteDelete cascade of the purge, the ILike
/// search) are proved against a real database in the integration suite, not faked here.
/// </summary>
public sealed class ServerRepositoryScopeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NowUtc = Now.UtcDateTime;

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly AppDbContext _db;
    private readonly ServerRepository _repository;

    public ServerRepositoryScopeTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options, _clock);
        _repository = new ServerRepository(_db, _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SaveAsync() => await _db.SaveChangesAsync(Ct);

    // Organization 7, as every project below: a server reaches only the projects of its own organization.
    private static Server Server(int id, string name) => new() { Id = id, Name = name, Hostname = name, OrganizationId = 7 };

    /// <summary>
    /// Server 1 is linked to project 10 and, separately, executed a pipeline step of project 11.
    /// Project 12 is unrelated. Every scope read must return exactly {10, 11}.
    /// </summary>
    private async Task SeedTwoWaysOfBeingConnectedAsync()
    {
        _db.Servers.AddRange(Server(1, "runner"), Server(2, "other"));
        _db.Projects.AddRange(
            new Project { Id = 10, Name = "linked", OrganizationId = 7 },
            new Project { Id = 11, Name = "executed", OrganizationId = 7 },
            new Project { Id = 12, Name = "unrelated", OrganizationId = 7 });
        _db.ProjectServers.Add(new ProjectServer { Id = 100, ProjectId = 10, ServerId = 1, DisplayName = "vps-1" });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 200, Name = "executed-pipeline", ProjectId = 11 },
            new Pipeline { Id = 201, Name = "elsewhere", ProjectId = 12 });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 300, PipelineId = 200 },
            new PipelineRun { Id = 301, PipelineId = 201 });
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { Id = 400, PipelineRunId = 300, ServerId = 1, StageName = "build", StepName = "compile" },
            new PipelineStepRun { Id = 401, PipelineRunId = 300, ServerId = 1, StageName = "build", StepName = "test" },
            new PipelineStepRun { Id = 402, PipelineRunId = 301, ServerId = 2, StageName = "build", StepName = "compile" });
        await SaveAsync();
        _db.ChangeTracker.Clear();
    }

    // ---------- single-server loads ----------

    [Fact]
    public async Task FindServerWithTokensAsync_LoadsTheTokensWithoutTrackingTheServer()
    {
        _db.Servers.Add(Server(1, "runner"));
        _db.ServerTokens.AddRange(
            new ServerToken { Id = 1, ServerId = 1, TokenHash = "a", ExpiresAt = NowUtc.AddDays(1) },
            new ServerToken { Id = 2, ServerId = 1, TokenHash = "b", ExpiresAt = NowUtc.AddDays(1) },
            new ServerToken { Id = 3, ServerId = 2, TokenHash = "c", ExpiresAt = NowUtc.AddDays(1) });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var server = await _repository.FindServerWithTokensAsync(1, Ct);

        Assert.Equal(2, server!.Tokens.Count);
        Assert.Empty(_db.ChangeTracker.Entries<Server>());
        Assert.Null(await _repository.FindServerWithTokensAsync(404, Ct));
    }

    [Fact]
    public async Task TryMarkOfflineIfStaleAsync_FlipsAnOnlineServerWhoseHeartbeatDidNotMove()
    {
        var observed = NowUtc.AddMinutes(-10);
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "runner",
            Hostname = "runner",
            Status = ServerStatus.Online,
            LastHeartbeat = observed
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var flipped = await _repository.TryMarkOfflineIfStaleAsync(1, observed, TimeSpan.FromMinutes(5), Ct);

        Assert.True(flipped);
        Assert.Equal(
            ServerStatus.Offline,
            (await _db.Servers.AsNoTracking().FirstAsync(server => server.Id == 1, Ct)).Status);
    }

    [Fact]
    public async Task TryMarkOfflineIfStaleAsync_RefusesWhenTheHeartbeatMovedSinceItWasObserved()
    {
        var observed = NowUtc.AddMinutes(-10);
        _db.Servers.Add(new Server
        {
            Id = 1,
            Name = "runner",
            Hostname = "runner",
            Status = ServerStatus.Online,
            LastHeartbeat = NowUtc.AddSeconds(-1)
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        Assert.False(await _repository.TryMarkOfflineIfStaleAsync(1, observed, TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(
            ServerStatus.Online,
            (await _db.Servers.AsNoTracking().FirstAsync(server => server.Id == 1, Ct)).Status);
    }

    // ---------- name listings ----------

    [Fact]
    public async Task GetServerNamesAsync_OrdersByNameAndHonoursTheAccessibleScope()
    {
        _db.Servers.AddRange(Server(1, "zeta"), Server(2, "alpha"), Server(3, "beta"));
        await SaveAsync();

        Assert.Equal(["alpha", "beta", "zeta"], await _repository.GetServerNamesAsync(ct: Ct));
        Assert.Equal(["alpha", "zeta"], await _repository.GetServerNamesAsync([1, 2], Ct));
        Assert.Equal(["alpha", "beta", "zeta"], await _repository.GetServerNamesAsync(Ct));
    }

    [Fact]
    public async Task GetServerIdNamePairsAsync_PairsEachNameWithItsIdInNameOrder()
    {
        _db.Servers.AddRange(Server(1, "zeta"), Server(2, "alpha"));
        await SaveAsync();

        Assert.Equal([(2, "alpha"), (1, "zeta")], await _repository.GetServerIdNamePairsAsync(ct: Ct));
        Assert.Equal([(1, "zeta")], await _repository.GetServerIdNamePairsAsync([1], Ct));
    }

    // ---------- what a server is connected to ----------

    [Fact]
    public async Task GetProjectIdsForServerAsync_UnionsTheLinkedAndTheExecutedProjects()
    {
        await SeedTwoWaysOfBeingConnectedAsync();

        var ids = await _repository.GetProjectIdsForServerAsync(1, Ct);

        Assert.Equal([10, 11], ids.Order());
    }

    [Fact]
    public async Task GetPipelineIdsForServerAsync_ReturnsEachExecutedPipelineOnce()
    {
        await SeedTwoWaysOfBeingConnectedAsync();

        Assert.Equal([200], await _repository.GetPipelineIdsForServerAsync(1, Ct));
        Assert.Equal([201], await _repository.GetPipelineIdsForServerAsync(2, Ct));
        Assert.Empty(await _repository.GetPipelineIdsForServerAsync(3, Ct));
    }

    [Fact]
    public async Task GetProjectsForServerAsync_MaterializesBothConnectionKindsInNameOrder()
    {
        await SeedTwoWaysOfBeingConnectedAsync();

        var projects = await _repository.GetProjectsForServerAsync(1, Ct);

        Assert.Equal(["executed", "linked"], projects.Select(project => project.Name));
    }

    [Fact]
    public async Task GetPipelinesForServerAsync_LoadsTheProjectAndTheRunsOfEachExecutedPipeline()
    {
        await SeedTwoWaysOfBeingConnectedAsync();

        var pipeline = Assert.Single(await _repository.GetPipelinesForServerAsync(1, Ct));

        Assert.Equal("executed-pipeline", pipeline.Name);
        Assert.Equal("executed", pipeline.Project!.Name);
        Assert.Single(pipeline.Runs);
    }

    [Fact]
    public async Task GetVariableLibrariesForServerAsync_ReachesTheServerThroughEveryOwnershipKind()
    {
        await SeedTwoWaysOfBeingConnectedAsync();
        _db.Environments.AddRange(
            new Environment { Id = 500, Name = "staging", ProjectId = 12 },
            new Environment { Id = 501, Name = "prod", ProjectId = 12 });
        _db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 500, ServerId = 1 });
        _db.EnvironmentProjectServers.Add(
            new EnvironmentProjectServer { EnvironmentId = 501, ProjectServerId = 100 });
        _db.VariableLibraries.AddRange(
            new VariableLibrary { Id = 1, Name = "by-linked-project", ProjectId = 10 },
            new VariableLibrary { Id = 2, Name = "by-executed-project", ProjectId = 11 },
            new VariableLibrary { Id = 3, Name = "by-project-server", ProjectServerId = 100 },
            new VariableLibrary { Id = 4, Name = "by-environment-server", EnvironmentId = 500 },
            new VariableLibrary { Id = 5, Name = "by-environment-project-server", EnvironmentId = 501 },
            // R-83: the server sits in an environment of project 12, so project 12 is one of its projects.
            new VariableLibrary { Id = 7, Name = "by-environment-server-project", ProjectId = 12 },
            new VariableLibrary { Id = 6, Name = "unrelated", ProjectId = 13 });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var libraries = await _repository.GetVariableLibrariesForServerAsync(1, Ct);

        Assert.Equal(
            [
                "by-environment-project-server",
                "by-environment-server",
                "by-environment-server-project",
                "by-executed-project",
                "by-linked-project",
                "by-project-server"
            ],
            libraries.Select(library => library.Name));
    }

    [Fact]
    public async Task GetVaultsForServerAsync_ReachesTheServerThroughEveryOwnershipKind()
    {
        await SeedTwoWaysOfBeingConnectedAsync();
        _db.Environments.Add(new Environment { Id = 500, Name = "staging", ProjectId = 12 });
        _db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = 500, ServerId = 1 });
        _db.Vaults.AddRange(
            new Vault { Id = 1, Name = "by-linked-project", ProjectId = 10 },
            new Vault { Id = 2, Name = "by-project-server", ProjectServerId = 100 },
            new Vault { Id = 3, Name = "by-environment-server", EnvironmentId = 500 },
            // R-83: the server sits in an environment of project 12, so project 12 is one of its projects.
            new Vault { Id = 5, Name = "by-environment-server-project", ProjectId = 12 },
            new Vault { Id = 4, Name = "unrelated", ProjectId = 13 });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var vaults = await _repository.GetVaultsForServerAsync(1, Ct);

        Assert.Equal(
            ["by-environment-server", "by-environment-server-project", "by-linked-project", "by-project-server"],
            vaults.Select(vault => vault.Name));
    }

    [Fact]
    public async Task GetServiceTypeAsync_ReadsTheTypeOfThatServersServiceOnly()
    {
        _db.ServiceInfos.AddRange(
            new ServiceInfo { Id = 1, ServerId = 1, Name = "docker", Type = ServiceType.Docker },
            new ServiceInfo { Id = 2, ServerId = 2, Name = "apache2", Type = ServiceType.Systemd });
        await SaveAsync();

        Assert.Equal(ServiceType.Docker, await _repository.GetServiceTypeAsync(1, "docker", Ct));
        Assert.Null(await _repository.GetServiceTypeAsync(1, "apache2", Ct));
        Assert.Null(await _repository.GetServiceTypeAsync(3, "docker", Ct));
    }

    // ---------- paged project listing ----------

    [Theory]
    [InlineData(null, false, new[] { "executed", "linked" })]
    [InlineData(null, true, new[] { "linked", "executed" })]
    [InlineData("Status", false, new[] { "executed", "linked" })]
    [InlineData("Status", true, new[] { "linked", "executed" })]
    public async Task GetProjectsForServerPagedAsync_SortsOnEverySupportedKey(
        string? sortBy, bool descending, string[] expected)
    {
        await SeedTwoWaysOfBeingConnectedAsync();
        var linked = await _db.Projects.FirstAsync(project => project.Id == 10, Ct);
        linked.Status = ProjectStatus.Archived;
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var (items, total) = await _repository.GetProjectsForServerPagedAsync(
            1, null, 1, 10, sortBy, descending, Ct);

        Assert.Equal(2, total);
        Assert.Equal(expected, items.Select(project => project.Name));
    }

    [Fact]
    public async Task GetProjectsForServerPagedAsync_SortsOnTheCreationInstant()
    {
        _db.Servers.Add(Server(1, "runner"));
        _clock.SetUtcNow(Now);
        _db.Projects.Add(new Project { Id = 10, Name = "older", OrganizationId = 7 });
        _db.ProjectServers.Add(new ProjectServer { Id = 100, ProjectId = 10, ServerId = 1, DisplayName = "a" });
        await SaveAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        _db.Projects.Add(new Project { Id = 11, Name = "newer", OrganizationId = 7 });
        _db.ProjectServers.Add(new ProjectServer { Id = 101, ProjectId = 11, ServerId = 1, DisplayName = "b" });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var ascending = await _repository.GetProjectsForServerPagedAsync(1, null, 1, 10, "CreatedAt", false, Ct);
        var descending = await _repository.GetProjectsForServerPagedAsync(1, null, 1, 10, "createdat", true, Ct);

        Assert.Equal(["older", "newer"], ascending.Items.Select(project => project.Name));
        Assert.Equal(["newer", "older"], descending.Items.Select(project => project.Name));
    }

    [Fact]
    public async Task GetProjectsForServerPagedAsync_PagesAfterOrderingAndKeepsTheUnpagedTotal()
    {
        _db.Servers.Add(Server(1, "runner"));
        for (var index = 1; index <= 5; index++)
        {
            _db.Projects.Add(new Project { Id = index, Name = $"project-{index:D2}", OrganizationId = 7 });
            _db.ProjectServers.Add(new ProjectServer
            {
                Id = 100 + index,
                ProjectId = index,
                ServerId = 1,
                DisplayName = $"link-{index}"
            });
        }
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var (items, total) = await _repository.GetProjectsForServerPagedAsync(1, null, 3, 2, null, false, Ct);

        Assert.Equal(5, total);
        Assert.Equal(["project-05"], items.Select(project => project.Name));
    }

    // ---------- tasks ----------

    [Fact]
    public async Task AddTasksAsync_CommitsTheWholeBatchInOneSave()
    {
        await _repository.AddTasksAsync(
            [
                new ServerTask { Id = 1, ServerId = 1, Name = "one" },
                new ServerTask { Id = 2, ServerId = 1, Name = "two" }
            ],
            Ct);

        Assert.Equal(2, await _db.Tasks.CountAsync(Ct));
    }

    [Fact]
    public async Task GetTasksPagedAsync_ReturnsThatServersTasksNewestFirst()
    {
        _db.Servers.Add(Server(1, "runner"));
        await SaveAsync();
        foreach (var index in Enumerable.Range(1, 3))
        {
            _clock.SetUtcNow(Now.AddMinutes(index));
            _db.Tasks.Add(new ServerTask { Id = index, ServerId = 1, Name = $"task-{index}" });
            await SaveAsync();
        }
        _db.Tasks.Add(new ServerTask { Id = 9, ServerId = 2, Name = "other-server" });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var (items, total) = await _repository.GetTasksPagedAsync(1, 1, 2, Ct);

        Assert.Equal(3, total);
        Assert.Equal(["task-3", "task-2"], items.Select(task => task.Name));
    }

    [Fact]
    public async Task CountActiveNonUpdateTasksAsync_IgnoresSelfUpdatesAndFinishedWork()
    {
        _db.Tasks.AddRange(
            new ServerTask { Id = 1, ServerId = 1, Status = TaskExecutionStatus.Pending },
            new ServerTask { Id = 2, ServerId = 1, Status = TaskExecutionStatus.Running },
            new ServerTask { Id = 3, ServerId = 1, Status = TaskExecutionStatus.Success },
            new ServerTask
            {
                Id = 4,
                ServerId = 1,
                Status = TaskExecutionStatus.Running,
                Operation = OperationKind.AgentSelfUpdate
            },
            new ServerTask { Id = 5, ServerId = 2, Status = TaskExecutionStatus.Running });
        await SaveAsync();

        Assert.Equal(2, await _repository.CountActiveNonUpdateTasksAsync(1, Ct));
    }

    [Fact]
    public async Task GetServersForCompatibilityAsync_ProjectsTheAgentFactsForTheRequestedScope()
    {
        _db.Servers.AddRange(
            new Server
            {
                Id = 1,
                Name = "runner",
                Hostname = "runner",
                AgentVersion = "1.0.926",
                AgentProtocolVersion = 2,
                Status = ServerStatus.Online,
                PipelineRunnerEnabled = true,
                DeploymentTargetAvailable = true,
                AgentCapabilitiesJson = "[\"agent.self-update\"]"
            },
            Server(2, "other"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var scoped = await _repository.GetServersForCompatibilityAsync([1], Ct);

        var only = Assert.Single(scoped);
        Assert.Equal("1.0.926", only.AgentVersion);
        Assert.Equal(2, only.AgentProtocolVersion);
        Assert.True(only.PipelineRunnerEnabled);
        Assert.True(only.DeploymentTargetAvailable);
        Assert.Contains("agent.self-update", only.AgentCapabilities);
        Assert.Equal(2, (await _repository.GetServersForCompatibilityAsync(null, Ct)).Count);
    }

    public void Dispose() => _db.Dispose();
}
