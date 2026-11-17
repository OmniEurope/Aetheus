// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-44: the project readiness (<c>GET api/pipelines/setup/project-readiness</c>) read the same rows again for every
/// pipeline of the project: its template, its requirements, its environment sample and its resolved variables. With
/// the readiness built as the application builds it, N pipelines that share a template, a sample and a library cost
/// one read of each, not N. The counts below are the number of calls the readiness makes on its collaborators (the
/// repositories and services that issue the SQL), on doubles: they prove the batching, not the timing of a database.
/// </summary>
public sealed class PipelineReadinessBatchingTests
{
    private const int ProjectId = 7;
    private const int OrganizationId = 3;
    private const int PipelineCount = 6;
    private const string SamplePath = ".pipeline/configs/env/production.env";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BaseTemplate = """
        name: shared-base
        variable_libraries:
          - w7.prod
        requires:
          libraries:
            - w7.prod
          environments:
            - prod
        variables:
          DEPLOY_ENV_SAMPLE: ".pipeline/configs/env/production.env"
        stages:
          - name: Deploy
            steps:
              - name: Render
                env_template: "$(DEPLOY_ENV_SAMPLE)"
                shell: echo $AETHEUS_ENV_FILE_B64
        """;

    private const string Sample = "APPNAME=#{APPNAME}#\n";

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IPipelineTemplateRepository _templates = Substitute.For<IPipelineTemplateRepository>();
    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();
    private readonly IProjectConfigRepositoryService _config = Substitute.For<IProjectConfigRepositoryService>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IPipelineVariableResolver _variables = ReadinessVariableResolver.Create();

    private IPipelineSetupReadinessService Sut()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IPipelineTemplateService>());
        services.AddSingleton(_pipelines);
        services.AddSingleton(_templates);
        services.AddSingleton(_git);
        services.AddSingleton(Substitute.For<IGitLightRepository>());
        services.AddSingleton(_requirements);
        services.AddSingleton(_config);
        services.AddSingleton(_variables);
        services.AddSingleton(Substitute.For<IPipelineSetupServerRepository>());
        return PipelinesModuleExtensions.CreateSetupReadinessService(services.BuildServiceProvider());
    }

    private void Project(string? sampleFromConfiguration)
    {
        var pipelines = Enumerable.Range(1, PipelineCount)
            .Select(index => (Id: 100 + index, Name: $"w7-pipeline-{index}", Yaml: $"name: w7-pipeline-{index}\nextends: shared-base\n"))
            .ToList();
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(pipelines);
        _pipelines.GetPipelineOrganizationIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(OrganizationId);
        _templates.FindTemplateByNameAsync("shared-base", OrganizationId, Arg.Any<CancellationToken>()).Returns(
            new PipelineTemplate
            {
                Id = 1,
                Name = "shared-base",
                OrganizationId = OrganizationId,
                LatestVersion = 1,
                Versions = [new PipelineTemplateVersion { TemplateId = 1, Version = 1, YamlContent = BaseTemplate }]
            });
        _requirements.CheckAsync(
                Arg.Any<PipelineRequiresDefinition?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        // One entry of the library is empty, so the readiness asks which samples read it: the second question per pipeline.
        _requirements.FindEmptyValuesAsync(
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns([new PipelineEmptyValue(PipelineRequirementsChecker.LibraryKind, "APPNAME", 9, "w7.prod")]);
        _pipelines.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _config.ReadAsync(ProjectId, SamplePath, null, null, Arg.Any<CancellationToken>()).Returns(sampleFromConfiguration);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(
            [new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "w7", DefaultBranch = "main", IsEmpty = false }]);
        _git.GetBlobAsync(4, "main", SamplePath, Arg.Any<CancellationToken>()).Returns(
            new GitLightBlobDto { Path = SamplePath, Content = Sample, IsBinary = false });
    }

    private int VariableResolutions() =>
        _variables.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IPipelineVariableResolver.ResolveVariablesWithWarningsAsync));

    [Fact]
    public async Task PipelinesSharingATemplateASampleAndALibrary_AreReadOnce_NotOncePerPipeline()
    {
        Project(Sample);

        var readiness = await Sut().CheckProjectAsync(ProjectId, pipelineId: null, OrganizationId, Ct);

        // The check really ran for every pipeline (the empty library entry is reported for each of them).
        var empty = Assert.Single(readiness.Checks, check => check.Kind == PipelineSetupReadinessKind.EmptyLibraryValues);
        Assert.Equal(PipelineCount, empty.EmptyValues.Count);

        // The template of the six pipelines: one row read (the shared base), where the resolver asked once per pipeline.
        await _templates.Received(1).FindTemplateByNameAsync("shared-base", OrganizationId, Arg.Any<CancellationToken>());
        // The declared requirements of the six definitions: one check, not six.
        await _requirements.Received(1).CheckAsync(
            Arg.Any<PipelineRequiresDefinition?>(), ProjectId, OrganizationId, Arg.Any<CancellationToken>());
        // The sample: read once. The empty-value check and the undefined-variable check each used to read it for every pipeline.
        await _config.Received(1).ReadAsync(ProjectId, SamplePath, null, null, Arg.Any<CancellationToken>());
        // The launch variables: resolved once per pipeline. Both checks used to resolve them, twelve times.
        Assert.Equal(PipelineCount, VariableResolutions());
    }

    [Fact]
    public async Task ASampleKeptInTheRepository_ListsTheRepositoryOnceAndReadsTheFileOnce()
    {
        Project(sampleFromConfiguration: null);

        await Sut().CheckProjectAsync(ProjectId, pipelineId: null, OrganizationId, Ct);

        // One listing for the adapter-script check and one for the samples, whatever the number of pipelines.
        await _git.Received(2).GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>());
        await _git.Received(1).GetBlobAsync(4, "main", SamplePath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheUnionOfDeclaredResources_KeepsEachNameOnce_InTheOrderTheDefinitionsFirstNameIt()
    {
        var first = new PipelineYamlDefinition
        {
            VariableLibraries = ["a", "B"],
            Requires = new PipelineRequiresDefinition { Environments = ["prod"], Capabilities = ["docker"] }
        };
        var second = new PipelineYamlDefinition
        {
            VariableLibraries = ["b", "c"],
            Vaults = ["v"],
            Requires = new PipelineRequiresDefinition { Environments = ["PROD", "qa"] }
        };

        var union = PipelineSetupReadinessService.UnionOfDeclaredResources([first, second]);

        Assert.Equal(["a", "B", "c"], union.Libraries);
        Assert.Equal(["v"], union.Vaults);
        Assert.Equal(["prod", "qa"], union.Environments);
        Assert.Equal(["docker"], union.Capabilities);
    }
}
