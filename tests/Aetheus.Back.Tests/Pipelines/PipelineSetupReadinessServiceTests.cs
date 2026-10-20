// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The pre-creation readiness check, which answers what the setup wizard is about to create against
/// what the project actually has.
///
/// Every case here defends one property: the findings are DERIVED from the selected templates (a
/// template that stops invoking a script stops requiring it), and an unread repository is reported
/// as unread rather than as a clean bill of health.
/// </summary>
public sealed class PipelineSetupReadinessServiceTests
{
    private readonly IPipelineTemplateService _templates = Substitute.For<IPipelineTemplateService>();
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();

    private const string CiTemplate = """
        name: application-ci-template
        stages:
          - name: Build
            execution_role: build
            steps:
              - name: Run project CI adapter
                shell: |
                  set -eu
                  test -f .pipeline/scripts/ci.sh
                  sh .pipeline/scripts/ci.sh
        """;

    private const string DeployTemplate = """
        name: application-deploy-prod-template
        stages:
          - name: DeployProduction
            execution_role: deploy
            steps:
              - name: Run project production deployment adapter
                shell: |
                  set -eu
                  test -f .pipeline/scripts/deploy-prod.sh
                  sh .pipeline/scripts/deploy-prod.sh
        """;

    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();

    private readonly IGitLightRepository _gitRepositories = Substitute.For<IGitLightRepository>();
    private readonly IPipelineTemplateResolver _resolver = Substitute.For<IPipelineTemplateResolver>();

    private PipelineSetupReadinessService BuildSut() => new(
        _templates, _repo, _git, _gitRepositories, _resolver, _requirements,
        Substitute.For<ILogger<PipelineSetupReadinessService>>());

    private void Template(int id, string name, string yaml)
    {
        _templates.GetTemplatesAsync(Arg.Any<CancellationToken>()).Returns(call =>
            new List<PipelineTemplateSummaryDto> { new() { Id = id, Name = name, Version = 1 } });
        _templates.GetTemplateAsync(id, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateDto { Id = id, Name = name, YamlContent = yaml });
    }

    private void Runners(params int[] ids)
        => _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ids.ToList());

    private void Repository(bool empty = false, string branch = "main")
        => _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(
            new List<GitLightRepoDto> { new() { Id = 4, ProjectId = 7, Name = "app", DefaultBranch = branch, IsEmpty = empty } });

    private void AdapterDirectory(params string[] fileNames)
        => _git.GetTreeAsync(4, Arg.Any<string?>(), ".pipeline/scripts", Arg.Any<CancellationToken>())
            .Returns([.. fileNames.Select(name => new GitLightTreeEntryDto
            {
                Name = name,
                Path = $".pipeline/scripts/{name}",
                Type = GitTreeEntryType.Blob
            })]);

    private Task<PipelineSetupReadinessDto> RunAsync(params string[] templateNames)
        => BuildSut().CheckAsync(7, templateNames, organizationId: 3, CancellationToken.None);

    [Fact]
    public async Task AdapterScriptTheTemplateInvokes_IsReportedWhenTheBranchDoesNotCarryIt()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        Repository();
        AdapterDirectory("quality.sh");

        var readiness = await RunAsync("application-ci");

        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.MissingAdapterScripts, check.Kind);
        Assert.Equal([".pipeline/scripts/ci.sh"], check.Items);
        Assert.True(readiness.HasBlocking);
        Assert.True(readiness.RepositoryInspected);
        Assert.Equal("main", readiness.InspectedBranch);
    }

    [Fact]
    public async Task AdapterScriptPresentOnTheBranch_ProducesNoFinding()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        Repository();
        AdapterDirectory("ci.sh");

        var readiness = await RunAsync("application-ci");

        Assert.Empty(readiness.Checks);
        Assert.False(readiness.HasBlocking);
        Assert.True(readiness.RepositoryInspected);
    }

    [Fact]
    public async Task ProjectWithoutRepository_SaysTheFilesWereNotInspectedRatherThanNothingIsMissing()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(new List<GitLightRepoDto>());

        var readiness = await RunAsync("application-ci");

        Assert.Equal(PipelineSetupReadinessKind.NoRepository, Assert.Single(readiness.Checks).Kind);
        // The distinction the panel depends on: no adapter finding was produced, and the caller is
        // told why, instead of an empty list reading as a pass.
        Assert.False(readiness.RepositoryInspected);
    }

    [Fact]
    public async Task EmptyRepository_NamesTheBranchAndStopsBeforeClaimingAnythingAboutFiles()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        Repository(empty: true, branch: "develop");

        var readiness = await RunAsync("application-ci");

        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.EmptyBranch, check.Kind);
        Assert.Equal(["develop"], check.Items);
        Assert.False(readiness.RepositoryInspected);
    }

    [Fact]
    public async Task NoServerAtAll_IsReportedOnceRatherThanAsBothAMissingRunnerAndAMissingTarget()
    {
        Template(2, "application-deploy-prod", DeployTemplate);
        Runners();
        Repository();
        AdapterDirectory("deploy-prod.sh");

        var readiness = await RunAsync("application-deploy-prod");

        Assert.Equal(PipelineSetupReadinessKind.NoRunnerConfigured, Assert.Single(readiness.Checks).Kind);
    }

    [Fact]
    public async Task DeployStageWithoutADeploymentCapableServer_NamesTheStagesThatWouldStall()
    {
        Template(2, "application-deploy-prod", DeployTemplate);
        // A build runner exists; the deployment-capable query is the one that comes back empty.
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 11 });
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), true, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<int>());
        Repository();
        AdapterDirectory("deploy-prod.sh");

        var readiness = await RunAsync("application-deploy-prod");

        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.NoDeployRunnerConfigured, check.Kind);
        Assert.Equal(["DeployProduction"], check.Items);
    }

    [Fact]
    public async Task EnvironmentNamedByTheTemplate_IsReportedWhenTheProjectDoesNotHaveIt()
    {
        Template(3, "application-qa", """
            name: application-qa-template
            stages:
              - name: QA
                environment: qa
                steps:
                  - name: Run
                    shell: make qa
            """);
        Runners(11);
        Repository();
        _repo.FindEnvironmentByNameForProjectAsync("qa", 7, Arg.Any<CancellationToken>())
            .Returns((Environment?)null);

        var readiness = await RunAsync("application-qa");

        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.MissingEnvironments, check.Kind);
        Assert.Equal(["qa"], check.Items);
    }

    [Fact]
    public async Task EnvironmentStillWrittenAsAVariable_IsNotGuessedAtAndProducesNoFinding()
    {
        Template(3, "application-qa", """
            name: application-qa-template
            stages:
              - name: QA
                environment: $(APPLICATION_ENVIRONMENT)
                steps:
                  - name: Run
                    shell: make qa
            """);
        Runners(11);
        Repository();

        var readiness = await RunAsync("application-qa");

        Assert.Empty(readiness.Checks);
        await _repo.DidNotReceive().FindEnvironmentByNameForProjectAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TemplateTheInstanceDoesNotHave_IsSkippedInsteadOfBecomingAnUnclearableFinding()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        Repository();
        AdapterDirectory("ci.sh");

        var readiness = await RunAsync("application-ci", "application-does-not-exist");

        Assert.Empty(readiness.Checks);
    }

    private const string SecurityTemplate = """
        name: application-security-template
        stages:
          - name: Scan source
            steps:
              - name: Scan secrets
                type: scanner
                scanner: gitleaks
              - name: Scan code (SAST)
                type: scanner
                scanner: opengrep
              - name: Scan IaC
                type: scanner
                scanner: trivy-iac
        """;

    /// <summary>Recette R3-63, option 3: Gitleaks reads .gitleaks.toml when the repository has one and
    /// its built-in rules otherwise, so a repository without it is ready. Trivy's ignore file is
    /// optional too: nothing is asked for.</summary>
    [Fact]
    public async Task GitleaksConfigurationAbsent_IsNotAskedFor()
    {
        Template(1, "application-security", SecurityTemplate);
        Runners(11);
        Repository();
        _git.GetTreeAsync(4, Arg.Any<string?>(), Arg.Is<string?>(path => path == null), Arg.Any<CancellationToken>())
            .Returns([new GitLightTreeEntryDto { Name = "README.md", Path = "README.md", Type = GitTreeEntryType.Blob }]);

        var readiness = await RunAsync("application-security");

        Assert.Empty(readiness.Checks);
        Assert.False(readiness.HasBlocking);
        Assert.True(readiness.RepositoryInspected);
    }

    [Fact]
    public async Task ScannerConfigurationPresentAtTheRoot_ProducesNoFinding()
    {
        Template(1, "application-security", SecurityTemplate);
        Runners(11);
        Repository();
        _git.GetTreeAsync(4, Arg.Any<string?>(), Arg.Is<string?>(path => path == null), Arg.Any<CancellationToken>())
            .Returns([new GitLightTreeEntryDto { Name = ".gitleaks.toml", Path = ".gitleaks.toml", Type = GitTreeEntryType.Blob }]);

        var readiness = await RunAsync("application-security");

        Assert.Empty(readiness.Checks);
    }

    /// <summary>A project whose source is an external address has a repository; Aetheus cannot read
    /// it, so the files are announced as not checkable, never as missing nor "no
    /// repository".</summary>
    [Fact]
    public async Task ExternalAddressWithoutAReadableCopy_IsNotNoRepository_ButFilesThatCannotBeChecked()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(new List<GitLightRepoDto>());
        _gitRepositories.GetProjectRepositorySourceAsync(7, Arg.Any<CancellationToken>())
            .Returns(new ProjectRepositorySource("https://github.com/acme/perf.git", null));

        var readiness = await RunAsync("application-ci");

        var check = Assert.Single(readiness.Checks);
        Assert.Equal(PipelineSetupReadinessKind.ExternalRepositoryNotInspected, check.Kind);
        Assert.Equal(PipelineSetupReadinessSeverity.Warning, check.Severity);
        Assert.Equal([".pipeline/scripts/ci.sh"], check.Items);
        Assert.False(readiness.HasBlocking);
        Assert.False(readiness.RepositoryInspected);
    }

    [Fact]
    public async Task ExternalSourceWhoseMirrorHoldsNoCommit_IsNotCalledAnEmptyBranch()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        Repository(empty: true, branch: "main");
        _gitRepositories.GetProjectRepositorySourceAsync(7, Arg.Any<CancellationToken>())
            .Returns(new ProjectRepositorySource("http://localhost/git/7/perf.git", 5));

        var readiness = await RunAsync("application-ci");

        Assert.Equal(PipelineSetupReadinessKind.ExternalRepositoryNotInspected, Assert.Single(readiness.Checks).Kind);
        Assert.False(readiness.HasBlocking);
    }

    [Fact]
    public async Task ExternalSourceWithASynchronizedMirror_IsReadLikeAnInternalRepository()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        Repository();
        AdapterDirectory("quality.sh");
        _gitRepositories.GetProjectRepositorySourceAsync(7, Arg.Any<CancellationToken>())
            .Returns(new ProjectRepositorySource("http://localhost/git/7/app.git", 5));

        var readiness = await RunAsync("application-ci");

        Assert.Equal(PipelineSetupReadinessKind.MissingAdapterScripts, Assert.Single(readiness.Checks).Kind);
        Assert.True(readiness.RepositoryInspected);
    }

    [Fact]
    public async Task AnExternalRepositoryAttachedBesideTheProjectsOwn_IsNotWhereItsAdaptersAreLookedFor()
    {
        Template(1, "application-ci", CiTemplate);
        Runners(11);
        _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(new List<GitLightRepoDto>
        {
            new() { Id = 9, ProjectId = 7, Name = "lib", DefaultBranch = "main", IsAdditionalSource = true }
        });

        var readiness = await RunAsync("application-ci");

        Assert.Equal(PipelineSetupReadinessKind.NoRepository, Assert.Single(readiness.Checks).Kind);
    }

    /// <summary>The project's existing pipelines are read as a run reads them, their template
    /// expanded, so a pipeline extending application-ci requires ci.sh.</summary>
    [Fact]
    public async Task ProjectReadiness_ExpandsEachPipelinesTemplate_AndReportsItsMissingAdapter()
    {
        const string pipelineYaml = "name: atlas-ci\nextends: application-ci@1\nstages: []\n";
        _repo.GetPipelineDefinitionsForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns([(21, "atlas-ci", pipelineYaml), (22, "atlas-deploy-prod", "name: atlas-deploy-prod\nextends: application-deploy-prod@1\nstages: []\n")]);
        _repo.GetPipelineOrganizationIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(3);
        _resolver.ResolveAsync(pipelineYaml, 3, null, Arg.Any<CancellationToken>())
            .Returns(Resolution(CiTemplate));
        _resolver.ResolveAsync(Arg.Is<string>(yaml => yaml.Contains("deploy-prod", StringComparison.Ordinal)), 3, null, Arg.Any<CancellationToken>())
            .Returns(Resolution(DeployTemplate));
        Runners(11);
        Repository();
        AdapterDirectory("deploy-prod.sh");

        var all = await BuildSut().CheckProjectAsync(7, pipelineId: null, organizationId: 3, CancellationToken.None);
        var deployOnly = await BuildSut().CheckProjectAsync(7, pipelineId: 22, organizationId: 3, CancellationToken.None);

        var check = Assert.Single(all.Checks);
        Assert.Equal(PipelineSetupReadinessKind.MissingAdapterScripts, check.Kind);
        Assert.Equal([".pipeline/scripts/ci.sh"], check.Items);
        // Narrowed to the deploy pipeline, whose adapter is present: nothing stops that run.
        Assert.Empty(deployOnly.Checks);
    }

    [Fact]
    public async Task ProjectReadiness_WithoutPipelines_ClaimsNothing()
    {
        _repo.GetPipelineDefinitionsForProjectAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<(int, string, string)>());

        var readiness = await BuildSut().CheckProjectAsync(7, pipelineId: null, organizationId: 3, CancellationToken.None);

        Assert.Empty(readiness.Checks);
        await _git.DidNotReceive().GetRepositoriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static PipelineTemplateResolution Resolution(string yaml) => new(
        YamlParsingHelper.ParseAndValidate(yaml, Substitute.For<ILogger>())!, yaml, null, null, null, false);
}
