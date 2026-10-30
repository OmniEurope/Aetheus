// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using DataEnvironment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Repositories;

/// <summary>
/// Recette R-83: a server attached to an environment of a project is a server of that project, for every reader
/// of "the servers of a project" and "the projects of a server". The user attaches it once, to the environment.
/// Each reader also proves the other side: an environment of another project, or of no project, adds nothing.
/// These run on EF InMemory; the PostgreSQL translation of the same LINQ is not exercised here.
/// </summary>
public sealed class ProjectServerEnvironmentScopeTests : IDisposable
{
    private readonly AppDbContext _db;
    private Project _mine = null!;
    private Project _other = null!;
    private DataEnvironment _myProd = null!;
    private DataEnvironment _myStaging = null!;
    private DataEnvironment _otherProd = null!;
    private DataEnvironment _loose = null!;
    private Server _onMyProd = null!;
    private Server _onMyProdAndStaging = null!;
    private Server _onOtherProd = null!;
    private Server _onLooseEnvironment = null!;
    private Server _linkedAndOnMyProd = null!;

    public ProjectServerEnvironmentScopeTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync(CancellationToken ct)
    {
        _mine = new Project { Name = "Mine", Description = "d" };
        _other = new Project { Name = "Other", Description = "d" };
        _db.Projects.AddRange(_mine, _other);
        await _db.SaveChangesAsync(ct);

        _myProd = new DataEnvironment { Name = "prod", ProjectId = _mine.Id };
        _myStaging = new DataEnvironment { Name = "staging", ProjectId = _mine.Id };
        _otherProd = new DataEnvironment { Name = "other-prod", ProjectId = _other.Id };
        _loose = new DataEnvironment { Name = "loose", ProjectId = null };
        _db.Environments.AddRange(_myProd, _myStaging, _otherProd, _loose);

        _onMyProd = new Server { Name = "on-my-prod", Hostname = "h1", Status = ServerStatus.Online };
        _onMyProdAndStaging = new Server { Name = "on-both", Hostname = "h2", Status = ServerStatus.Online };
        _onOtherProd = new Server { Name = "on-other-prod", Hostname = "h3", Status = ServerStatus.Online };
        _onLooseEnvironment = new Server { Name = "on-loose", Hostname = "h4", Status = ServerStatus.Online };
        _linkedAndOnMyProd = new Server { Name = "linked-and-env", Hostname = "h5", Status = ServerStatus.Online };
        _db.Servers.AddRange(_onMyProd, _onMyProdAndStaging, _onOtherProd, _onLooseEnvironment, _linkedAndOnMyProd);
        await _db.SaveChangesAsync(ct);

        _db.EnvironmentServers.AddRange(
            new EnvironmentServer { EnvironmentId = _myProd.Id, ServerId = _onMyProd.Id },
            new EnvironmentServer { EnvironmentId = _myProd.Id, ServerId = _onMyProdAndStaging.Id },
            new EnvironmentServer { EnvironmentId = _myStaging.Id, ServerId = _onMyProdAndStaging.Id },
            new EnvironmentServer { EnvironmentId = _otherProd.Id, ServerId = _onOtherProd.Id },
            new EnvironmentServer { EnvironmentId = _loose.Id, ServerId = _onLooseEnvironment.Id },
            new EnvironmentServer { EnvironmentId = _myProd.Id, ServerId = _linkedAndOnMyProd.Id });
        _db.ProjectServers.Add(new ProjectServer
        {
            ProjectId = _mine.Id,
            ServerId = _linkedAndOnMyProd.Id,
            Type = ProjectServerType.AgentServer,
            DisplayName = "linked-and-env (project link)",
            Host = "h5"
        });
        _db.ProjectServers.Add(new ProjectServer
        {
            ProjectId = _mine.Id,
            Type = ProjectServerType.ExternalHost,
            DisplayName = "ext",
            Host = "ext.example"
        });
        await _db.SaveChangesAsync(ct);
    }

    private ProjectRepository ProjectRepo() => new(_db, Substitute.For<IProjectAnalysisGradeReader>());

    [Fact]
    public async Task ProjectRepository_GetProjectServersAsync_CountsTheServersOfTheProjectsEnvironments()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var rows = await ProjectRepo().GetProjectServersAsync(_mine.Id, ct);

        Assert.Equal(
            ["ext", "linked-and-env (project link)", "on-both", "on-my-prod"],
            rows.Select(row => row.DisplayName).Order(StringComparer.Ordinal));
        var viaEnvironment = rows.Where(row => row.ViaEnvironmentId is not null).ToList();
        Assert.All(viaEnvironment, row => Assert.Equal(0, row.Id));
        Assert.Equal(_myProd.Id, viaEnvironment.Single(row => row.ServerId == _onMyProd.Id).ViaEnvironmentId);
        Assert.Equal("prod", viaEnvironment.Single(row => row.ServerId == _onMyProd.Id).ViaEnvironmentName);
        // In two environments of the project: listed once, under the first one.
        Assert.Equal(_myProd.Id, Assert.Single(viaEnvironment, row => row.ServerId == _onMyProdAndStaging.Id).ViaEnvironmentId);
        // Linked to the project and on its environment: the project link wins, no second line.
        var linked = Assert.Single(rows, row => row.ServerId == _linkedAndOnMyProd.Id);
        Assert.NotEqual(0, linked.Id);
        Assert.Null(linked.ViaEnvironmentId);
    }

    [Fact]
    public async Task ProjectRepository_GetProjectServersAsync_NeverReturnsAnotherProjectsEnvironmentServer()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var rows = await ProjectRepo().GetProjectServersAsync(_mine.Id, ct);

        Assert.DoesNotContain(rows, row => row.ServerId == _onOtherProd.Id);
        Assert.DoesNotContain(rows, row => row.ServerId == _onLooseEnvironment.Id);
        var otherRows = await ProjectRepo().GetProjectServersAsync(_other.Id, ct);
        Assert.Equal(_onOtherProd.Id, Assert.Single(otherRows).ServerId);
    }

    [Fact]
    public async Task ProjectRepository_GetProjectServersPageAsync_CountsPagesSearchesAndFiltersTheEnvironmentServers()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var repo = ProjectRepo();

        var (all, total) = await repo.GetProjectServersPageAsync(_mine.Id, null, null, false, 1, 10, ct);
        var (secondPage, secondTotal) = await repo.GetProjectServersPageAsync(_mine.Id, null, null, false, 2, 3, ct);
        var (searched, searchedTotal) = await repo.GetProjectServersPageAsync(_mine.Id, "ON-MY", null, false, 1, 10, ct);
        var (agents, agentsTotal) = await repo.GetProjectServersPageAsync(
            _mine.Id, null, null, false, 1, 10, ct,
            [new GridFilter { Field = "Type", Operator = GridFilterOperator.In, Value = "AgentServer" }]);

        Assert.Equal(4, total);
        Assert.Equal(4, all.Count);
        Assert.Equal(4, secondTotal);
        Assert.Single(secondPage);
        Assert.Equal(1, searchedTotal);
        Assert.Equal(_onMyProd.Id, Assert.Single(searched).ServerId);
        Assert.Equal(3, agentsTotal);
        Assert.DoesNotContain(agents, row => row.DisplayName == "ext");
        Assert.DoesNotContain(all, row => row.ServerId == _onOtherProd.Id);
    }

    [Fact]
    public async Task ProjectRepository_GetProjectServerAsync_StillFindsOnlyRealProjectServerLinks()
    {
        // The writers (update, delete) read through this method: an environment server has no row to find.
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        Assert.Null(await ProjectRepo().GetProjectServerAsync(_mine.Id, 0, ct));
        var onlyEnvironment = await ProjectRepo().GetProjectServersAsync(_mine.Id, ct);
        Assert.All(onlyEnvironment.Where(row => row.ViaEnvironmentId is not null),
            row => Assert.Empty(_db.ProjectServers.Where(projectServer => projectServer.Id == row.Id)));
    }

    [Fact]
    public async Task AiTaskRepository_ResolveExecutionServerAsync_PicksAnOnlineServerOfTheProjectsEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var onlyEnvironmentProject = new Project { Name = "Env only", Description = "d" };
        _db.Projects.Add(onlyEnvironmentProject);
        await _db.SaveChangesAsync(ct);
        var environment = new DataEnvironment { Name = "prod", ProjectId = onlyEnvironmentProject.Id };
        var offline = new Server { Name = "offline", Hostname = "o", Status = ServerStatus.Offline };
        var online = new Server { Name = "online", Hostname = "n", Status = ServerStatus.Online };
        _db.Environments.Add(environment);
        _db.Servers.AddRange(offline, online);
        await _db.SaveChangesAsync(ct);
        _db.EnvironmentServers.AddRange(
            new EnvironmentServer { EnvironmentId = environment.Id, ServerId = offline.Id },
            new EnvironmentServer { EnvironmentId = environment.Id, ServerId = online.Id });
        await _db.SaveChangesAsync(ct);
        var repo = new AiTaskRepository(_db);

        var chosen = await repo.ResolveExecutionServerAsync(
            new AiTaskDefinition { ProjectId = onlyEnvironmentProject.Id }, ct);
        var none = await repo.ResolveExecutionServerAsync(new AiTaskDefinition { ProjectId = null }, ct);

        Assert.Equal(online.Id, chosen!.Id);
        Assert.Null(none);
    }

    [Fact]
    public async Task AiTaskRepository_ResolveExecutionServerAsync_NeverPicksAnotherProjectsEnvironmentServer()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var project = new Project { Name = "No servers", Description = "d" };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(ct);

        var chosen = await new AiTaskRepository(_db).ResolveExecutionServerAsync(
            new AiTaskDefinition { ProjectId = project.Id }, ct);

        Assert.Null(chosen);
    }

    [Fact]
    public async Task ReleaseRepository_GetReleasesForServerAsync_IncludesTheReleasesOfTheProjectOfTheServersEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        _db.Releases.AddRange(
            new Release { ProjectId = _mine.Id, Version = "mine-1", BranchName = "main" },
            new Release { ProjectId = _other.Id, Version = "other-1", BranchName = "main" });
        await _db.SaveChangesAsync(ct);
        var repo = new ReleaseRepository(_db);

        var onMyProd = await repo.GetReleasesForServerAsync(_onMyProd.Id, ct);
        var onOtherProd = await repo.GetReleasesForServerAsync(_onOtherProd.Id, ct);
        var onLoose = await repo.GetReleasesForServerAsync(_onLooseEnvironment.Id, ct);

        Assert.Equal("mine-1", Assert.Single(onMyProd).Version);
        Assert.Equal("other-1", Assert.Single(onOtherProd).Version);
        Assert.Empty(onLoose);
    }

    [Fact]
    public async Task ServerRepository_GetProjectIdsForServerAsync_IncludesTheProjectOfTheServersEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var repo = new ServerRepository(_db, TimeProvider.System);

        Assert.Equal([_mine.Id], await repo.GetProjectIdsForServerAsync(_onMyProd.Id, ct));
        Assert.Equal([_mine.Id], await repo.GetProjectIdsForServerAsync(_onMyProdAndStaging.Id, ct));
        Assert.Equal([_mine.Id], await repo.GetProjectIdsForServerAsync(_linkedAndOnMyProd.Id, ct));
        Assert.Equal([_other.Id], await repo.GetProjectIdsForServerAsync(_onOtherProd.Id, ct));
        Assert.Empty(await repo.GetProjectIdsForServerAsync(_onLooseEnvironment.Id, ct));
    }

    [Fact]
    public async Task UserNotificationRepository_GetServerProjectIdsAsync_IncludesTheProjectOfTheServersEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var repo = new UserNotificationRepository(_db);

        Assert.Equal([_mine.Id], await repo.GetServerProjectIdsAsync(_onMyProd.Id, ct));
        Assert.Equal([_mine.Id], await repo.GetServerProjectIdsAsync(_linkedAndOnMyProd.Id, ct));
        Assert.Equal([_other.Id], await repo.GetServerProjectIdsAsync(_onOtherProd.Id, ct));
        Assert.Empty(await repo.GetServerProjectIdsAsync(_onLooseEnvironment.Id, ct));
    }
}
