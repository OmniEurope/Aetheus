// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using DataEnvironment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-63: the single server of the project's production environment is the default of the deployment's
/// <c>deployServer</c>, resolved when the pipeline is read for a launch, even when the environment was created after the
/// pipeline and for the deployment a candidate chains to.</summary>
public sealed class PipelineProductionServerDefaultTests
{
    private const int ProjectId = 7;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string DeployYaml = """
        name: w7-deploy-prod
        trigger: manual
        parameters:
          - name: deployServer
            type: string
            required: true
        stages:
          - name: Deploy
            agent: "${{ parameters.deployServer }}"
            steps:
              - name: Restart
                shell: echo restart
        """;

    private static DataEnvironment Production(int? projectId, params string[] servers) => new()
    {
        Id = 3,
        Name = "prod",
        ProjectId = projectId,
        Servers = [.. servers.Select(name => new EnvironmentServer { Server = new Server { Name = name } })]
    };

    private static IPipelineEnvironmentReader Reader(DataEnvironment? production)
    {
        var reader = Substitute.For<IPipelineEnvironmentReader>();
        reader.FindEnvironmentByNameAsync("prod", ProjectId, Arg.Any<CancellationToken>()).Returns(production);
        return reader;
    }

    private static PipelineYamlDefinition Definition(string? defaultValue = null, params string[] allowed) => new()
    {
        Name = "w7-deploy-prod",
        Parameters =
        [
            new PipelineTemplateParameterDefinition
            {
                Name = "deployServer", Type = allowed.Length > 0 ? "choice" : "string", Required = true,
                Default = defaultValue, AllowedValues = [.. allowed]
            },
            new PipelineTemplateParameterDefinition { Name = "candidateVersion", Required = true }
        ]
    };

    [Fact]
    public async Task TheOnlyServerOfTheProductionEnvironment_BecomesTheDefault()
    {
        var result = await PipelineProductionServerDefault.ApplyAsync(Reader(Production(ProjectId, "vps")), Definition(), ProjectId, Ct);

        Assert.Equal("vps", result.Parameters.Single(parameter => parameter.Name == "deployServer").Default);
        Assert.Null(result.Parameters.Single(parameter => parameter.Name == "candidateVersion").Default);
    }

    [Fact]
    public async Task AChoiceGainsTheServerAmongItsAllowedValues_SoTheDefaultIsOneItAccepts()
    {
        var result = await PipelineProductionServerDefault.ApplyAsync(
            Reader(Production(ProjectId, "vps")), Definition(null, "old-vps"), ProjectId, Ct);

        var parameter = result.Parameters.Single(item => item.Name == "deployServer");
        Assert.Equal("vps", parameter.Default);
        Assert.Equal(["old-vps", "vps"], parameter.AllowedValues);
    }

    [Fact]
    public async Task ADefaultTheYamlNames_IsNeverReplaced()
    {
        var definition = Definition("chosen");

        var result = await PipelineProductionServerDefault.ApplyAsync(Reader(Production(ProjectId, "vps")), definition, ProjectId, Ct);

        Assert.Same(definition, result);
    }

    [Fact]
    public async Task NothingIsGuessedAmongSeveralServers_OrFromAnEnvironmentThatIsNotTheProjects_OrWithoutAnEnvironment()
    {
        var definition = Definition();

        Assert.Same(definition, await PipelineProductionServerDefault.ApplyAsync(
            Reader(Production(ProjectId, "vps", "backup")), definition, ProjectId, Ct));
        Assert.Same(definition, await PipelineProductionServerDefault.ApplyAsync(
            Reader(Production(null, "shared")), definition, ProjectId, Ct));
        Assert.Same(definition, await PipelineProductionServerDefault.ApplyAsync(Reader(null), definition, ProjectId, Ct));
        Assert.Same(definition, await PipelineProductionServerDefault.ApplyAsync(Reader(Production(ProjectId, "vps")), definition, null, Ct));
    }

    [Fact]
    public async Task APipelineWithoutADeployServerParameter_IsLeftAlone()
    {
        var definition = new PipelineYamlDefinition { Name = "w7-ci" };

        Assert.Same(definition, await PipelineProductionServerDefault.ApplyAsync(
            Reader(Production(ProjectId, "vps")), definition, ProjectId, Ct));
    }

    /// <summary>The real preparation and the real template resolver: the pipeline was written without a server, the
    /// environment came later, and the run parameters the launch dialog reads and the launch resolves carry its server.</summary>
    [Fact]
    public async Task ThePreparationOfALaunch_ResolvesTheDefaultFromTheEnvironmentCreatedAfterThePipeline()
    {
        const string url = "https://github.com/example/w7.git";
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var repo = Substitute.For<IPipelineRepository>();
        var gitCli = Substitute.For<IGitCliService>();
        var pipeline = new Pipeline
        {
            Id = 3,
            ProjectId = ProjectId,
            Project = new Project { Id = ProjectId, RepositoryUrl = url, DefaultBranch = "main" },
            Name = "w7-deploy-prod",
            YamlDefinition = DeployYaml
        };
        repo.FindPipelineAsync(3, Ct).Returns(pipeline);
        repo.GetPipelineOrganizationIdAsync(3, Ct).Returns(1);
        repo.FindEnvironmentByNameAsync("prod", ProjectId, Ct).Returns(Production(ProjectId, "vps"));
        gitCli.ResolveBranchCommitAsync(url, "main", Ct).Returns(new GitRemoteBranch("main", commit));
        gitCli.ReadPipelineYamlAsync(url, commit, pipeline.Name, Ct).Returns((string?)null);
        var sut = new PipelineRunPreparationService(
            repo, Substitute.For<IPipelineGitService>(), new PipelineTemplateResolver(repo),
            Substitute.For<IPipelineRunParameterResolver>(), gitCli, Substitute.For<IPipelineWorkspaceSourceResolver>(),
            new ConfigurationBuilder().Build(), NullLogger<PipelineRunPreparationService>.Instance);

        var preparation = await sut.PrepareRunAsync(3, null, null, null, Ct);
        var offered = await sut.GetRunParametersAsync(3, null, Ct);

        Assert.NotNull(preparation);
        Assert.Equal("vps", preparation.Definition.Parameters.Single().Default);
        Assert.Equal("vps", offered.Single(parameter => parameter.Name == "deployServer").Default);
    }

    /// <summary>The deployment a candidate chains to has no parameter of its own at launch: its server is the environment's.</summary>
    [Fact]
    public async Task TheChainedPipelineOfAPreview_ReadsTheServerOfTheEnvironmentAsItsDefault()
    {
        var templates = Substitute.For<IPipelineTemplateResolver>();
        var target = new Pipeline { Id = 4, ProjectId = ProjectId, Name = "w7-deploy-prod", YamlDefinition = DeployYaml };
        templates.ResolveAsync(DeployYaml, 1, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateResolution(Definition(), DeployYaml, null, null, null, false));
        var sut = new PipelineChildPipelineResolver(templates, Substitute.For<IPipelineVariableResolver>(), Reader(Production(ProjectId, "vps")));

        var child = await sut.ResolveDefinitionAsync(target, 1, "w7-deploy-prod", [], Ct);

        Assert.NotNull(child);
        Assert.Equal("vps", child.Parameters.Single(parameter => parameter.Name == "deployServer").Default);
    }
}
