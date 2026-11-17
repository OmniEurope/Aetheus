// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using DataEnvironment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-36: a candidate that starts the production deployment on success while the deployment requires a server it is not
/// given (the chained launch passes only the release) is told before any run, on the project's readiness, not only on the
/// candidate run after it succeeded and nothing followed. Same decision as the launch (<c>PipelineChainedDeployServer.Decide</c>).
/// </summary>
public sealed class PipelineChainedDeployServerReadinessTests
{
    private const int ProjectId = 21;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Candidate = """
        name: oe1-candidate
        trigger: manual
        on_success:
          - pipeline: oe1-deploy-prod
            release: latest
        stages:
          - name: Build
            steps:
              - name: Build
                shell: echo build
        """;

    private static string DeployProd(string serverParameter) => $$"""
        name: oe1-deploy-prod
        trigger: manual
        parameters:
          - name: deployServer
            type: string
            required: true
        {{serverParameter}}
        stages:
          - name: Deploy
            steps:
              - name: Deploy
                shell: echo deploy
        """;

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IPipelineSetupServerRepository _servers = Substitute.For<IPipelineSetupServerRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();

    public PipelineChainedDeployServerReadinessTests()
    {
        _pipelines.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(
            [new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "oe1", DefaultBranch = "main", IsEmpty = false }]);
    }

    private async Task<PipelineSetupReadinessCheckDto?> CheckAsync(string deployProdYaml, params string[] projectServers) =>
        await CheckWithProductionAsync(deployProdYaml, production: null, projectServers);

    private async Task<PipelineSetupReadinessCheckDto?> CheckWithProductionAsync(
        string deployProdYaml, DataEnvironment? production, params string[] projectServers)
    {
        _pipelines.FindEnvironmentByNameAsync("prod", ProjectId, Arg.Any<CancellationToken>()).Returns(production);
        _servers.ListProjectServerNamesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([.. projectServers]);
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([(181, "oe1-candidate", Candidate), (182, "oe1-deploy-prod", deployProdYaml)]);
        var sut = new PipelineSetupReadinessService(
            Substitute.For<IPipelineTemplateService>(), _pipelines, _git, Substitute.For<IGitLightRepository>(),
            Substitute.For<IPipelineTemplateResolver>(), Substitute.For<IPipelineRequirementsChecker>(),
            Substitute.For<IProjectConfigRepositoryService>(), ReadinessVariableResolver.Create(), _servers,
            NullLogger<PipelineSetupReadinessService>.Instance);
        var readiness = await sut.CheckProjectAsync(ProjectId, pipelineId: 181, organizationId: 3, Ct);
        return readiness.Checks.SingleOrDefault(check => check.Kind == PipelineSetupReadinessKind.ChainedDeployServerMissing);
    }

    [Fact]
    public async Task ADeploymentWithoutServerDefault_InAProjectWithTwoServers_IsSaidWillNotStart()
    {
        var check = await CheckAsync(DeployProd(string.Empty), "vps1", "vps2");

        Assert.NotNull(check);
        Assert.Equal(["oe1-candidate > oe1-deploy-prod"], check.Items);
        // R-21: an error (not a discreet note), yet never Blocking: the candidate run itself is not at risk, only the follow-up is.
        Assert.Equal(PipelineSetupReadinessSeverity.Error, check.Severity);
        // R-21: the item leads to the follow-up pipeline (its id), where the default is set.
        Assert.Equal(182, check.ItemIds["oe1-candidate > oe1-deploy-prod"]);
    }

    [Fact]
    public async Task ADeploymentWithoutServerDefault_InAProjectWithNoServer_IsSaidWillNotStart()
    {
        Assert.NotNull(await CheckAsync(DeployProd(string.Empty)));
    }

    [Fact]
    public async Task ADeploymentWithoutServerDefault_InAProjectWithOneServer_IsNotSaid_TheLaunchUsesIt()
    {
        Assert.Null(await CheckAsync(DeployProd(string.Empty), "vps1"));
    }

    [Fact]
    public async Task ADeploymentThatCarriesTheServerAsDefault_IsNotSaid()
    {
        var withDefault = DeployProd(string.Empty).Replace("required: true", "required: true\n    default: \"vps1\"", StringComparison.Ordinal);

        Assert.Null(await CheckAsync(withDefault, "vps1", "vps2"));
    }

    [Fact]
    public async Task ADeploymentThatDeclaresNoServerParameter_IsNotSaid()
    {
        var none = DeployProd(string.Empty).Replace("""
            parameters:
              - name: deployServer
                type: string
                required: true
            """, string.Empty, StringComparison.Ordinal);

        Assert.Null(await CheckAsync(none, "vps1", "vps2"));
    }

    private static DataEnvironment Production(int? projectId, params string[] servers) => new()
    {
        Id = 3,
        Name = "prod",
        ProjectId = projectId,
        Servers = [.. servers.Select(name => new EnvironmentServer { Server = new Server { Name = name } })]
    };

    /// <summary>R-20: the launch reads the follow-up with the server of the project's production environment as the
    /// default of <c>deployServer</c> (R-63), so a project holding several servers but a production environment with one is
    /// not told that the follow-up cannot start.</summary>
    [Fact]
    public async Task ADeploymentWithoutServerDefault_WhoseProductionEnvironmentHasOneServer_IsNotSaid_EvenWithTwoProjectServers()
    {
        Assert.Null(await CheckWithProductionAsync(DeployProd(string.Empty), Production(ProjectId, "vps1"), "vps1", "vps2"));
    }

    [Fact]
    public async Task ADeploymentWithoutServerDefault_WhoseProductionEnvironmentHasTwoServers_IsStillSaid()
    {
        Assert.NotNull(await CheckWithProductionAsync(DeployProd(string.Empty), Production(ProjectId, "vps1", "vps2"), "vps1", "vps2"));
    }

    [Fact]
    public async Task ADeploymentWithoutServerDefault_WhoseProductionEnvironmentIsAnotherProjects_IsStillSaid()
    {
        Assert.NotNull(await CheckWithProductionAsync(DeployProd(string.Empty), Production(99, "vps1"), "vps1", "vps2"));
    }
}
