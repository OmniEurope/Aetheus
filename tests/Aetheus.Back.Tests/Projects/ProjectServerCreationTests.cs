// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests.Projects;

/// <summary>Adding a server to a project: an agent server must name an existing server, an external host
/// never keeps a server id; the saved row claims its port under its own owner key, is audited, broadcast
/// to the project's organization, and comes back reloaded with its server.</summary>
public sealed class ProjectServerCreationTests
{
    private const int ProjectId = 1;
    private const int SavedId = 12;

    private readonly IProjectRepository _repo = Substitute.For<IProjectRepository>();
    private readonly IServerLifecycleService _servers = Substitute.For<IServerLifecycleService>();
    private readonly IPortRegistryService _ports = Substitute.For<IPortRegistryService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEntityChangeNotifier _notifier = Substitute.For<IEntityChangeNotifier>();
    private readonly ProjectService _sut;

    public ProjectServerCreationTests()
    {
        _sut = new ProjectService(_repo, _audit, _notifier, Substitute.For<IOrganizationService>(), _servers,
            TimeProvider.System, Substitute.For<IMemoryCache>(), _ports,
            Substitute.For<Aetheus.Back.Components.Notifications.IUserNotificationService>());
        _repo.FindProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns(new Project { Id = ProjectId, Name = "P", OrganizationId = 4 });
        _repo.When(repo => repo.AddProjectServerAsync(Arg.Any<ProjectServer>(), Arg.Any<CancellationToken>()))
            .Do(call => call.Arg<ProjectServer>().Id = SavedId);
    }

    private void Reloads(ProjectServer saved) =>
        _repo.GetProjectServerAsync(ProjectId, SavedId, Arg.Any<CancellationToken>()).Returns(saved);

    [Fact]
    public async Task AnAgentServerThatDoesNotExist_IsNotFoundAndNothingIsAdded()
    {
        _servers.ServerExistsAsync(30, Arg.Any<CancellationToken>()).Returns(false);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => _sut.CreateProjectServerAsync(ProjectId,
            new CreateProjectServerRequest { Type = ProjectServerType.AgentServer, ServerId = 30, DisplayName = "api", Host = "h" },
            TestContext.Current.CancellationToken));

        Assert.Equal("Server 30 not found.", error.Message);
        await _repo.DidNotReceive().AddProjectServerAsync(Arg.Any<ProjectServer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnExistingAgentServer_IsAddedClaimsItsPortAndComesBackWithItsServer()
    {
        _servers.ServerExistsAsync(30, Arg.Any<CancellationToken>()).Returns(true);
        Reloads(new ProjectServer
        {
            Id = SavedId,
            ProjectId = ProjectId,
            ServerId = 30,
            Type = ProjectServerType.AgentServer,
            DisplayName = "api",
            Host = "h",
            Port = 8080,
            Server = new Server { Id = 30, Name = "srv-30", Hostname = "srv-30.lan" },
        });

        var dto = await _sut.CreateProjectServerAsync(ProjectId,
            new CreateProjectServerRequest { Type = ProjectServerType.AgentServer, ServerId = 30, DisplayName = "api", Host = "h", Port = 8080, Notes = "n" },
            TestContext.Current.CancellationToken);

        await _repo.Received(1).AddProjectServerAsync(
            Arg.Is<ProjectServer>(row => row.ProjectId == ProjectId && row.ServerId == 30 && row.Port == 8080 && row.Notes == "n"),
            Arg.Any<CancellationToken>());
        await _ports.Received(1).SyncOwnedPortAsync(30, 8080, PortRegistryService.ProjectServerOwnerKey(SavedId), "api", ProjectId, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Created", "ProjectServer", SavedId, "api", Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(ResourceType.Project, ProjectId, EntityChangeOps.Updated, Arg.Any<CancellationToken>(), 4);
        Assert.Equal(SavedId, dto.Id);
        Assert.Equal("srv-30", dto.ServerName);
        Assert.Equal("srv-30.lan", dto.ServerHostname);
    }

    [Fact]
    public async Task AnExternalHost_DropsAServerIdWithoutCheckingIt()
    {
        Reloads(new ProjectServer { Id = SavedId, ProjectId = ProjectId, Type = ProjectServerType.ExternalHost, DisplayName = "cdn", Host = "cdn.example" });

        var dto = await _sut.CreateProjectServerAsync(ProjectId,
            new CreateProjectServerRequest { Type = ProjectServerType.ExternalHost, ServerId = 30, DisplayName = "cdn", Host = "cdn.example" },
            TestContext.Current.CancellationToken);

        await _servers.DidNotReceive().ServerExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddProjectServerAsync(Arg.Is<ProjectServer>(row => row.ServerId == null), Arg.Any<CancellationToken>());
        await _ports.Received(1).SyncOwnedPortAsync(null, null, PortRegistryService.ProjectServerOwnerKey(SavedId), "cdn", ProjectId, Arg.Any<CancellationToken>());
        Assert.Null(dto.ServerName);
        Assert.Equal("cdn.example", dto.Host);
    }
}
