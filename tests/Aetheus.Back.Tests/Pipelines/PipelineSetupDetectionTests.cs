// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The readiness check reads the branch files (and the fleet) for the generic templates' default commands:
/// a .NET solution for CI and quality, a .NET test project for QA, a Compose file and Docker for the
/// production deployment. A missing one is a finding raised before the pipelines exist; a pipeline that
/// declares its own commands is not asked; what could not be read is reported as not told, never as missing.
/// </summary>
public sealed class PipelineSetupDetectionTests
{
    private const int RepositoryId = 4;

    private readonly IPipelineTemplateService _templates = Substitute.For<IPipelineTemplateService>();
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IGitLightRepository _gitRepositories = Substitute.For<IGitLightRepository>();
    private readonly IPipelineTemplateResolver _resolver = Substitute.For<IPipelineTemplateResolver>();
    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();

    private static string TemplateYaml(string name, string commandsVariable) => $$"""
        name: {{name}}-template
        variables:
          {{commandsVariable}}: "default"
        stages:
          - name: Build
            execution_role: build
            steps:
              - name: Run
                shell: echo ok
        """;

    public PipelineSetupDetectionTests()
    {
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _repo.HasRunnerWithDockerAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(
            new List<GitLightRepoDto> { new() { Id = RepositoryId, ProjectId = 7, Name = "app", DefaultBranch = "main" } });
        _git.GetTreeAsync(RepositoryId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GitLightTreeEntryDto>());
    }

    private PipelineSetupReadinessService BuildSut() => new(
        _templates, _repo, _git, _gitRepositories, _resolver, _requirements, Substitute.For<IProjectConfigRepositoryService>(),
        Substitute.For<ILogger<PipelineSetupReadinessService>>());

    private void Templates(params (int Id, string Name, string Variable)[] templates)
    {
        foreach (var item in templates)
            _repo.FindSharedTemplateByNameAsync(item.Name, Arg.Any<CancellationToken>())
                .Returns(new Aetheus.Back.Data.Entities.PipelineTemplate { Id = item.Id, Name = item.Name });
        foreach (var item in templates)
            _templates.GetTemplateAsync(item.Id, Arg.Any<CancellationToken>())
                .Returns(new PipelineTemplateDto { Id = item.Id, Name = item.Name, YamlContent = TemplateYaml(item.Name, item.Variable) });
    }

    /// <summary>One directory of the branch: the names ending with a slash are sub-directories.</summary>
    private void Dir(string? path, params string[] names) =>
        _git.GetTreeAsync(RepositoryId, Arg.Any<string?>(), Arg.Is<string?>(candidate => candidate == path), Arg.Any<CancellationToken>())
            .Returns([.. names.Select(name =>
            {
                var directory = name.EndsWith('/');
                var bare = name.TrimEnd('/');
                return new GitLightTreeEntryDto
                {
                    Name = bare,
                    Path = path is null ? bare : $"{path}/{bare}",
                    Type = directory ? GitTreeEntryType.Tree : GitTreeEntryType.Blob
                };
            })]);

    private void Blob(string path, string content) =>
        _git.GetBlobAsync(RepositoryId, Arg.Any<string>(), path, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = path, Content = content, Size = content.Length });

    private Task<PipelineSetupReadinessDto> RunAsync(params string[] templateNames) =>
        BuildSut().CheckAsync(7, templateNames, organizationId: 3, CancellationToken.None);

    [Fact]
    public async Task RepositoryWithoutSolution_IsReportedForTheCiAndQualityDefaults()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"), (2, "application-quality", "APPLICATION_QUALITY_COMMANDS"));
        Dir(null, "README.md", "src/");
        Dir("src", "app.py");

        var readiness = await RunAsync("application-ci", "application-quality");

        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.NoDotNetSolution, check.Kind);
        Assert.Equal(["APPLICATION_CI_COMMANDS", "APPLICATION_QUALITY_COMMANDS"], check.Items);
        Assert.False(readiness.Detection.HasDotNetSolution);
        Assert.Null(readiness.Detection.HasDotNetTestProject);
        Assert.Null(readiness.Detection.HasComposeFile);
    }

    [Fact]
    public async Task SolutionAtTheRoot_IsFound()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"));
        Dir(null, "app.slnx");

        var readiness = await RunAsync("application-ci");

        Assert.Empty(readiness.Checks);
        Assert.True(readiness.Detection.HasDotNetSolution);
    }

    [Fact]
    public async Task SolutionTwoDirectoriesDown_IsFound()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"));
        Dir(null, "src/");
        Dir("src", "app/");
        Dir("src/app", "app.sln");

        var readiness = await RunAsync("application-ci");

        Assert.Empty(readiness.Checks);
        Assert.True(readiness.Detection.HasDotNetSolution);
    }

    [Fact]
    public async Task SolutionDeeperThanTheCommandsLook_DoesNotCount()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"));
        Dir(null, "a/");
        Dir("a", "b/");
        Dir("a/b", "c/");
        Dir("a/b/c", "deep.sln");

        var readiness = await RunAsync("application-ci");

        Assert.Equal(PipelineSetupReadinessKind.NoDotNetSolution, Assert.Single(readiness.Checks).Kind);
    }

    [Fact]
    public async Task QaWithoutAnyTestProject_IsReported_AndAnXunitProjectClearsIt()
    {
        Templates((3, "application-qa", "APPLICATION_QA_COMMANDS"));
        Dir(null, "app.sln", "src/", "tests/");
        Dir("src", "App.csproj");
        Dir("tests", "App.Tests/");
        Dir("tests/App.Tests", "App.Tests.csproj");
        Blob("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Blob("tests/App.Tests/App.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>");

        var withTests = await RunAsync("application-qa");

        Assert.Empty(withTests.Checks);
        Assert.True(withTests.Detection.HasDotNetTestProject);

        Blob("tests/App.Tests/App.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"Serilog\" /></ItemGroup></Project>");
        var withoutTests = await RunAsync("application-qa");

        var check = Assert.Single(withoutTests.Checks);
        Assert.Equal(PipelineSetupReadinessKind.NoDotNetTestProject, check.Kind);
        Assert.Equal(["APPLICATION_QA_COMMANDS"], check.Items);
        Assert.False(withoutTests.Detection.HasDotNetTestProject);
    }

    [Fact]
    public async Task QaWithOnlyABrowserEndToEndProject_CountsAsNoTestProject()
    {
        Templates((3, "application-qa", "APPLICATION_QA_COMMANDS"));
        Dir(null, "app.sln", "E2E.csproj");
        Blob("E2E.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /><PackageReference Include=\"Microsoft.Playwright\" /></ItemGroup></Project>");

        var readiness = await RunAsync("application-qa");

        Assert.Equal(PipelineSetupReadinessKind.NoDotNetTestProject, Assert.Single(readiness.Checks).Kind);
    }

    [Fact]
    public async Task QaWhenAProjectFileCannotBeRead_IsNotToldRatherThanMissing()
    {
        Templates((3, "application-qa", "APPLICATION_QA_COMMANDS"));
        Dir(null, "app.sln", "App.csproj");
        _git.GetBlobAsync(RepositoryId, Arg.Any<string>(), "App.csproj", Arg.Any<CancellationToken>())
            .Returns((GitLightBlobDto?)null);

        var readiness = await RunAsync("application-qa");

        Assert.Empty(readiness.Checks);
        Assert.Null(readiness.Detection.HasDotNetTestProject);
    }

    [Fact]
    public async Task ProductionDeployWithoutComposeFile_NamesTheFilesLookedFor()
    {
        Templates((4, "application-deploy-prod", "APPLICATION_DEPLOY_PROD_COMMANDS"));
        Dir(null, "app.sln", "Dockerfile");

        var readiness = await RunAsync("application-deploy-prod");

        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.NoComposeFile, check.Kind);
        Assert.Contains("compose.yaml", check.Items);
        Assert.Contains("docker-compose.yml", check.Items);
        Assert.False(readiness.Detection.HasComposeFile);
        Assert.True(readiness.Detection.HasDockerRunner);
    }

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("compose.yml")]
    [InlineData("docker-compose.yaml")]
    [InlineData("docker-compose.yml")]
    public async Task ProductionDeployWithAComposeFileAtTheRoot_IsReady(string composeFile)
    {
        Templates((4, "application-deploy-prod", "APPLICATION_DEPLOY_PROD_COMMANDS"));
        Dir(null, composeFile);

        var readiness = await RunAsync("application-deploy-prod");

        Assert.Empty(readiness.Checks);
        Assert.True(readiness.Detection.HasComposeFile);
    }

    [Fact]
    public async Task ProductionDeployWithoutDockerOnAnyRunner_IsReported()
    {
        Templates((4, "application-deploy-prod", "APPLICATION_DEPLOY_PROD_COMMANDS"));
        Dir(null, "compose.yaml");
        _repo.HasRunnerWithDockerAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);

        var readiness = await RunAsync("application-deploy-prod");

        Assert.Equal(PipelineSetupReadinessKind.NoDockerRunner, Assert.Single(readiness.Checks).Kind);
        Assert.False(readiness.Detection.HasDockerRunner);
    }

    [Fact]
    public async Task ProjectPipelineThatDeclaresItsOwnCommands_IsNotAskedAboutTheDefault()
    {
        const string ownCommands = "name: app-ci\nextends: application-ci@1\nvariables:\n  APPLICATION_CI_COMMANDS: \"make build\"\nstages: []\n";
        const string defaultCommands = "name: app-quality\nextends: application-quality@1\nstages: []\n";
        _repo.GetPipelineDefinitionsForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns([(21, "app-ci", ownCommands), (22, "app-quality", defaultCommands)]);
        _repo.GetPipelineOrganizationIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(3);
        _resolver.ResolveAsync(ownCommands, 3, null, Arg.Any<CancellationToken>())
            .Returns(Resolution(TemplateYaml("application-ci", "APPLICATION_CI_COMMANDS").Replace("\"default\"", "\"make build\"", StringComparison.Ordinal)));
        _resolver.ResolveAsync(defaultCommands, 3, null, Arg.Any<CancellationToken>())
            .Returns(Resolution(TemplateYaml("application-quality", "APPLICATION_QUALITY_COMMANDS")));
        Dir(null, "README.md");

        var readiness = await BuildSut().CheckProjectAsync(7, pipelineId: null, organizationId: 3, CancellationToken.None);

        // Only the quality pipeline runs the default build, so only it is said to fail without a solution.
        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.NoDotNetSolution, check.Kind);
        Assert.Equal(["APPLICATION_QUALITY_COMMANDS"], check.Items);
    }

    [Fact]
    public async Task UnreadableRepository_ClaimsNothingAboutItsFiles()
    {
        Templates((3, "application-qa", "APPLICATION_QA_COMMANDS"), (4, "application-deploy-prod", "APPLICATION_DEPLOY_PROD_COMMANDS"));
        _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(new List<GitLightRepoDto>());

        var readiness = await RunAsync("application-qa", "application-deploy-prod");

        Assert.False(readiness.RepositoryInspected);
        Assert.Null(readiness.Detection.HasDotNetTestProject);
        Assert.Null(readiness.Detection.HasComposeFile);
        Assert.DoesNotContain(readiness.Checks, check => check.Kind is PipelineSetupReadinessKind.NoDotNetTestProject
            or PipelineSetupReadinessKind.NoComposeFile);
    }

    private static PipelineTemplateResolution Resolution(string yaml) => new(
        YamlParsingHelper.ParseAndValidate(yaml, Substitute.For<ILogger>())!, yaml, null, null, null, false);
}
