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
        ReadinessVariableResolver.Create(), Substitute.For<IPipelineSetupServerRepository>(), Substitute.For<ILogger<PipelineSetupReadinessService>>());

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
        Dir(null, "app.slnx", "global.json");

        var readiness = await RunAsync("application-ci");

        Assert.Empty(readiness.Checks);
        Assert.True(readiness.Detection.HasDotNetSolution);
    }

    [Fact]
    public async Task R30_DotNetRepositoryWithoutGlobalJson_IsWarnedAbout_WithoutBlocking()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"));
        Dir(null, "app.slnx", "src/");

        var readiness = await RunAsync("application-ci");

        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.NoGlobalJson, check.Kind);
        Assert.Equal(PipelineSetupReadinessSeverity.Warning, check.Severity);
        Assert.Equal(["global.json"], check.Items);
        Assert.False(readiness.HasBlocking);
        Assert.False(readiness.Detection.HasGlobalJson);
    }

    [Fact]
    public async Task R30_DotNetRepositoryWithGlobalJson_IsNotWarnedAbout()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"));
        Dir(null, "app.slnx", "global.json");

        var readiness = await RunAsync("application-ci");

        Assert.Empty(readiness.Checks);
        Assert.True(readiness.Detection.HasGlobalJson);
    }

    [Fact]
    public async Task R30_RepositoryWithoutDotNet_IsNeverAskedForGlobalJson()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"));
        Dir(null, "package.json");

        var readiness = await RunAsync("application-ci");

        Assert.DoesNotContain(readiness.Checks, check => check.Kind == PipelineSetupReadinessKind.NoGlobalJson);
        Assert.Null(readiness.Detection.HasGlobalJson);
    }

    [Fact]
    public async Task R30_QaOnARepositoryWithATestProjectAndNoGlobalJson_IsWarnedAbout()
    {
        Templates((3, "application-qa", "APPLICATION_QA_COMMANDS"));
        Dir(null, "App.Tests.csproj");
        Blob("App.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>");

        var readiness = await RunAsync("application-qa");

        Assert.Equal(PipelineSetupReadinessKind.NoGlobalJson, Assert.Single(readiness.Checks).Kind);
    }

    [Fact]
    public async Task SolutionTwoDirectoriesDown_IsFound()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"));
        Dir(null, "src/", "global.json");
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
        Dir(null, "app.sln", "src/", "tests/", "global.json");
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

    private const string E2eVariable = "APPLICATION_QA_E2E_COMMANDS";

    /// <summary>Recette R-59: the browser tests the QA pipeline runs against its deployed stack are found in the repository,
    /// so the setup wizard writes their command itself: Playwright or Cypress in a <c>package.json</c>, the directory of the
    /// one found, and what was read to the end and holds none.</summary>
    [Theory]
    [InlineData("", "@playwright/test", "playwright")]
    [InlineData("web", "cypress", "cypress")]
    [InlineData("tests/e2e", "playwright", "playwright")]
    public async Task BrowserTests_APlaywrightOrCypressPackage_IsFoundWithItsDirectory(string directory, string dependency, string tool)
    {
        Templates((3, "application-qa", E2eVariable));
        // The chain of directories from the root down to the one that holds the package file.
        var segments = directory.Length == 0 ? [] : directory.Split('/');
        if (segments.Length == 0) Dir(null, "package.json");
        else Dir(null, segments[0] + "/");
        for (var level = 1; level < segments.Length; level++)
            Dir(string.Join('/', segments[..level]), segments[level] + "/");
        if (segments.Length > 0) Dir(directory, "package.json");
        var path = directory.Length == 0 ? "package.json" : $"{directory}/package.json";
        Blob(path, $"{{\"devDependencies\": {{\"{dependency}\": \"^1.0.0\"}}}}");

        var readiness = await RunAsync("application-qa");

        Assert.Empty(readiness.Checks);
        Assert.True(readiness.Detection.HasBrowserTests);
        Assert.Equal(tool, readiness.Detection.BrowserTestTool);
        Assert.Equal(directory, readiness.Detection.BrowserTestDirectory);
    }

    [Fact]
    public async Task BrowserTests_ARepositoryReadToTheEndWithNone_IsToldAsNone_NeverAsAFinding()
    {
        Templates((3, "application-qa", E2eVariable));
        Dir(null, "app.sln", "package.json", "node_modules/");
        Blob("package.json", "{\"dependencies\": {\"express\": \"4\"}, \"devDependencies\": {\"vitest\": \"1\"}}");

        var readiness = await RunAsync("application-qa");

        Assert.Empty(readiness.Checks);
        Assert.False(readiness.Detection.HasBrowserTests);
        Assert.Null(readiness.Detection.BrowserTestTool);
    }

    [Fact]
    public async Task BrowserTests_APackageFileThatCannotBeRead_IsNotToldRatherThanNone()
    {
        Templates((3, "application-qa", E2eVariable));
        Dir(null, "package.json");
        _git.GetBlobAsync(RepositoryId, Arg.Any<string>(), "package.json", Arg.Any<CancellationToken>())
            .Returns((GitLightBlobDto?)null);

        var readiness = await RunAsync("application-qa");

        Assert.Null(readiness.Detection.HasBrowserTests);
    }

    [Fact]
    public async Task BrowserTests_AreNotLookedFor_WhenNoSelectedPipelineReadsThem()
    {
        Templates((1, "application-ci", "APPLICATION_CI_COMMANDS"));
        Dir(null, "app.sln", "package.json");

        var readiness = await RunAsync("application-ci");

        Assert.Null(readiness.Detection.HasBrowserTests);
    }

    private const string PlaywrightProject =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.Playwright.NUnit\" />"
        + "<PackageReference Include=\"NUnit\" /></ItemGroup></Project>";

    /// <summary>A .NET project that references Microsoft.Playwright (its version central, in Directory.Packages.props) is the
    /// repository's browser test, found with its project file, the framework its build folder is named after (here from the
    /// Directory.Build.props above it) and the runner <c>global.json</c> selects, so the wizard writes a real command.</summary>
    [Fact]
    public async Task BrowserTests_ADotNetPlaywrightProject_IsFound_WithItsFrameworkAndRunner()
    {
        Templates((3, "application-qa", E2eVariable));
        Dir(null, "app.slnx", "tests/", "Directory.Build.props", "Directory.Packages.props", "global.json");
        Dir("tests", "App.E2E/", "App.Tests/");
        Dir("tests/App.E2E", "App.E2E.csproj", "Tests.cs");
        Dir("tests/App.Tests", "App.Tests.csproj");
        Blob("tests/App.E2E/App.E2E.csproj", PlaywrightProject);
        Blob("tests/App.Tests/App.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        Blob("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Blob("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Microsoft.Playwright.NUnit\" Version=\"1.64.1\" /></ItemGroup></Project>");
        Blob("global.json", "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }");

        var readiness = await RunAsync("application-qa");

        Assert.Empty(readiness.Checks);
        Assert.True(readiness.Detection.HasBrowserTests);
        Assert.Equal("playwright-dotnet", readiness.Detection.BrowserTestTool);
        Assert.Equal("tests/App.E2E", readiness.Detection.BrowserTestDirectory);
        Assert.Equal("tests/App.E2E/App.E2E.csproj", readiness.Detection.BrowserTestProject);
        Assert.Equal("net10.0", readiness.Detection.BrowserTestFramework);
        Assert.True(readiness.Detection.BrowserTestUsesTestingPlatform);
    }

    [Fact]
    public async Task BrowserTests_ADotNetPlaywrightProject_TakesItsOwnFramework_AndTheVsTestRunnerWithoutGlobalJson()
    {
        Templates((3, "application-qa", E2eVariable));
        Dir(null, "E2E.csproj", "Directory.Build.props");
        Blob("E2E.csproj", PlaywrightProject.Replace("<ItemGroup>", "<PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup><ItemGroup>", StringComparison.Ordinal));
        Blob("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var readiness = await RunAsync("application-qa");

        Assert.True(readiness.Detection.HasBrowserTests);
        Assert.Equal(string.Empty, readiness.Detection.BrowserTestDirectory);
        Assert.Equal("E2E.csproj", readiness.Detection.BrowserTestProject);
        Assert.Equal("net9.0", readiness.Detection.BrowserTestFramework);
        Assert.Null(readiness.Detection.BrowserTestUsesTestingPlatform);
    }

    [Fact]
    public async Task BrowserTests_ADotNetPlaywrightProject_WithNoFrameworkAnywhere_IsFoundWithoutOne()
    {
        Templates((3, "application-qa", E2eVariable));
        Dir(null, "E2E.csproj");
        Blob("E2E.csproj", PlaywrightProject);

        var readiness = await RunAsync("application-qa");

        Assert.True(readiness.Detection.HasBrowserTests);
        Assert.Equal("playwright-dotnet", readiness.Detection.BrowserTestTool);
        Assert.Null(readiness.Detection.BrowserTestFramework);
    }

    [Fact]
    public async Task BrowserTests_APlaywrightVersionOnlyInTheCentralFile_IsNoBrowserTest()
    {
        Templates((3, "application-qa", E2eVariable));
        Dir(null, "App.csproj", "Directory.Packages.props");
        Blob("App.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        Blob("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Microsoft.Playwright\" Version=\"1.64.1\" /></ItemGroup></Project>");

        var readiness = await RunAsync("application-qa");

        Assert.False(readiness.Detection.HasBrowserTests);
        Assert.Null(readiness.Detection.BrowserTestTool);
    }

    [Fact]
    public async Task BrowserTests_AProjectFileThatCannotBeRead_IsNotToldRatherThanNone()
    {
        Templates((3, "application-qa", E2eVariable));
        Dir(null, "App.csproj");
        _git.GetBlobAsync(RepositoryId, Arg.Any<string>(), "App.csproj", Arg.Any<CancellationToken>())
            .Returns((GitLightBlobDto?)null);

        var readiness = await RunAsync("application-qa");

        Assert.Null(readiness.Detection.HasBrowserTests);
    }

    [Fact]
    public async Task BrowserTests_APackageJsonPlaywright_StillComesBeforeADotNetOne()
    {
        Templates((3, "application-qa", E2eVariable));
        Dir(null, "package.json", "E2E.csproj");
        Blob("package.json", "{\"devDependencies\": {\"@playwright/test\": \"^1.0.0\"}}");
        Blob("E2E.csproj", PlaywrightProject);

        var readiness = await RunAsync("application-qa");

        Assert.Equal("playwright", readiness.Detection.BrowserTestTool);
    }

    private static PipelineTemplateResolution Resolution(string yaml) => new(
        YamlParsingHelper.ParseAndValidate(yaml, Substitute.For<ILogger>())!, yaml, null, null, null, false);
}
