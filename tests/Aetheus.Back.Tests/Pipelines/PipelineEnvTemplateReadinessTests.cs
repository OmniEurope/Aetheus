// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-68: a library entry the environment sample asks for as <c>#{NAME}#</c> and that is still empty is a value to
/// fill, on the project's overview as in the launch: the pipeline never writes it as <c>$(NAME)</c>, so the readiness has to
/// read the sample itself.</summary>
public sealed class PipelineEnvTemplateReadinessTests
{
    private const int ProjectId = 7;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string DeployYaml = """
        name: w7-deploy-prod
        variable_libraries:
          - w7.prod
        variables:
          DEPLOY_ENV_SAMPLE: ".pipeline/configs/env/production.env"
        stages:
          - name: Deploy
            steps:
              - name: Render
                env_template: "$(DEPLOY_ENV_SAMPLE)"
                shell: echo $AETHEUS_ENV_FILE_B64
        """;

    private const string Sample = "# the production environment\nAPPNAME=#{APPNAME}#\nJWT_ISSUER=#{JWT_ISSUER}#\nADMIN_EMAIL=#{ADMIN_EMAIL}#\n";

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();
    private readonly IProjectConfigRepositoryService _config = Substitute.For<IProjectConfigRepositoryService>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();

    private PipelineSetupReadinessService Sut() => new(
        Substitute.For<IPipelineTemplateService>(), _pipelines, _git, Substitute.For<IGitLightRepository>(),
        Substitute.For<IPipelineTemplateResolver>(), _requirements, _config, NullLogger<PipelineSetupReadinessService>.Instance);

    private void Project(params PipelineEmptyValue[] empty)
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([(21, "w7-deploy-prod", DeployYaml)]);
        _requirements.FindEmptyValuesAsync(
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(empty.ToList());
        _pipelines.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(
            [new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "w7", DefaultBranch = "main", IsEmpty = false }]);
    }

    [Fact]
    public async Task AnEmptyEntryTheEnvironmentSampleAsksFor_IsAValueToFill()
    {
        Project(
            new PipelineEmptyValue(PipelineRequirementsChecker.LibraryKind, "JWT_ISSUER", 12, "w7.prod"),
            new PipelineEmptyValue(PipelineRequirementsChecker.LibraryKind, "NOT_IN_THE_SAMPLE", 12, "w7.prod"));
        _config.ReadAsync(ProjectId, ".pipeline/configs/env/production.env", null, null, Arg.Any<CancellationToken>()).Returns(Sample);

        var readiness = await Sut().CheckProjectAsync(ProjectId, pipelineId: null, organizationId: 3, Ct);

        var library = Assert.Single(readiness.Checks, check => check.Kind == PipelineSetupReadinessKind.EmptyLibraryValues);
        Assert.Equal(["JWT_ISSUER"], library.Items);
        var row = Assert.Single(library.EmptyValues);
        Assert.Equal(("w7.prod", 12, "JWT_ISSUER", 21, "w7-deploy-prod"), (row.OwnerName, row.OwnerId, row.Key, row.PipelineId, row.PipelineName));
    }

    [Fact]
    public async Task TheSampleOfTheProjectsOwnSource_IsReadWhenTheConfigurationRepositoryHasNone()
    {
        Project(new PipelineEmptyValue(PipelineRequirementsChecker.LibraryKind, "ADMIN_EMAIL", 12, "w7.prod"));
        _config.ReadAsync(ProjectId, ".pipeline/configs/env/production.env", null, null, Arg.Any<CancellationToken>()).Returns((string?)null);
        _git.GetBlobAsync(4, "main", ".pipeline/configs/env/production.env", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = ".pipeline/configs/env/production.env", Content = Sample, IsBinary = false });

        var readiness = await Sut().CheckProjectAsync(ProjectId, pipelineId: null, organizationId: 3, Ct);

        Assert.Equal(["ADMIN_EMAIL"], Assert.Single(readiness.Checks, check => check.Kind == PipelineSetupReadinessKind.EmptyLibraryValues).Items);
    }

    [Fact]
    public async Task WithoutAnyReadableSample_NothingIsGuessed()
    {
        Project(new PipelineEmptyValue(PipelineRequirementsChecker.LibraryKind, "JWT_ISSUER", 12, "w7.prod"));
        _config.ReadAsync(ProjectId, ".pipeline/configs/env/production.env", null, null, Arg.Any<CancellationToken>()).Returns((string?)null);

        var readiness = await Sut().CheckProjectAsync(ProjectId, pipelineId: null, organizationId: 3, Ct);

        Assert.DoesNotContain(readiness.Checks, check => check.Kind == PipelineSetupReadinessKind.EmptyLibraryValues);
    }
}
