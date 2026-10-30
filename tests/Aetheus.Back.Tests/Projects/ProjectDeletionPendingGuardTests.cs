// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Projects;

/// <summary> Recette R-51 (ADR-058): a project deleted while something it deployed is still on a server is kept
/// only so its cleanup can be relaunched. It runs nothing (a run would deploy again what the cleanup removes)
/// and nothing changes it. </summary>
public sealed class ProjectDeletionPendingGuardTests
{
    private static readonly DateTime Since = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ARun_OfAPipelineOfAProjectWaitingForItsCleanup_IsRefused_BeforeAnythingIsRead()
    {
        var repo = Substitute.For<IPipelineRepository>();
        var gitCli = Substitute.For<IGitCliService>();
        var pipelineGit = Substitute.For<IPipelineGitService>();
        // An environment-owned pipeline: its project is found through the environment.
        var pipeline = new Pipeline { Id = 3, EnvironmentId = 21, Name = "deploy", YamlDefinition = "name: deploy\nstages: []\n" };
        repo.FindPipelineAsync(3, Arg.Any<CancellationToken>()).Returns(pipeline);
        repo.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(7);
        repo.IsProjectDeletionPendingAsync(7, Arg.Any<CancellationToken>()).Returns(true);
        var sut = new PipelineRunPreparationService(
            repo, pipelineGit, new PipelineTemplateResolver(repo), Substitute.For<IPipelineRunParameterResolver>(), gitCli,
            Substitute.For<IPipelineWorkspaceSourceResolver>(),
            new ConfigurationBuilder().Build(), NullLogger<PipelineRunPreparationService>.Instance);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => sut.PrepareRunAsync(3, null, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("deleted project", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(gitCli.ReceivedCalls());
        Assert.Empty(pipelineGit.ReceivedCalls());
    }

    [Fact]
    public async Task TheProject_AndItsServers_CannotBeChanged_WhileItWaitsForItsCleanup()
    {
        var repo = Substitute.For<IProjectRepository>();
        var project = new Project { Id = 7, Name = "Portfolio", OrganizationId = 1, DeletionRequestedAt = Since, Pipelines = [] };
        var link = new ProjectServer { Id = 12, ProjectId = 7, DisplayName = "prod", Type = ProjectServerType.ExternalHost };
        repo.FindProjectWithPipelinesAsync(7, Arg.Any<CancellationToken>()).Returns(project);
        repo.FindProjectAsync(7, Arg.Any<CancellationToken>()).Returns(project);
        repo.GetProjectServerAsync(7, 12, Arg.Any<CancellationToken>()).Returns(link);
        var sut = new ProjectService(repo, Substitute.For<IAuditService>(), Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IOrganizationService>(), Substitute.For<IServerLifecycleService>(), TimeProvider.System,
            Substitute.For<IMemoryCache>(), Substitute.For<Aetheus.Back.Components.PortRegistry.IPortRegistryService>(),
            Substitute.For<Aetheus.Back.Components.Notifications.IUserNotificationService>());
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ConflictException>(() => sut.UpdateProjectAsync(7, new UpdateProjectRequest { Name = "Renamed" }, ct));
        await Assert.ThrowsAsync<ConflictException>(() => sut.CreateProjectServerAsync(7,
            new CreateProjectServerRequest { Type = ProjectServerType.ExternalHost, DisplayName = "qa", Host = "qa.example" }, ct));
        await Assert.ThrowsAsync<ConflictException>(() => sut.UpdateProjectServerAsync(7, 12,
            new UpdateProjectServerRequest { DisplayName = "prod2", Host = "prod.example" }, ct));
        await Assert.ThrowsAsync<ConflictException>(() => sut.DeleteProjectServerAsync(7, 12, ct));

        Assert.Equal("Portfolio", project.Name);
        Assert.Equal("prod", link.DisplayName);
        await repo.DidNotReceiveWithAnyArgs().SaveChangesAsync(ct);
        await repo.DidNotReceiveWithAnyArgs().AddProjectServerAsync(default!, ct);
        await repo.DidNotReceiveWithAnyArgs().RemoveProjectServerAsync(default!, ct);
    }
}
