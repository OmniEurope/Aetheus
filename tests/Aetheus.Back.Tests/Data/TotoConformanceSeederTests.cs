// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.DataSeed;

public sealed class TotoConformanceSeederTests
{
    [Fact]
    public void EmbeddedRepository_ContainsEveryPipelineAndBothApplicationVersions()
    {
        var pipelines = TotoConformanceSeeder.LoadPipelineFiles();
        var vulnerablePipelines = TotoConformanceSeeder.LoadVulnerablePipelineFiles();
        var baseline = TotoConformanceSeeder.MergeRepositoryFiles("v1", pipelines);
        var current = TotoConformanceSeeder.MergeRepositoryFiles("v2", pipelines);
        var vulnerable = TotoConformanceSeeder.MergeVulnerableRepositoryFiles(vulnerablePipelines);

        Assert.Equal(TotoConformanceSeeder.PipelineNames.Length, pipelines.Count);
        Assert.All(TotoConformanceSeeder.PipelineNames, name =>
            Assert.Contains($".pipeline/{name}.yaml", pipelines.Keys));
        Assert.Equal(TotoConformanceSeeder.VulnerablePipelineNames.Length, vulnerablePipelines.Count);
        Assert.All(TotoConformanceSeeder.VulnerablePipelineNames, name =>
            Assert.Contains($".pipeline/{name}.yaml", vulnerablePipelines.Keys));

        foreach (var repository in new[] { baseline, current })
        {
            Assert.Contains("Toto.slnx", repository.Keys);
            Assert.Contains(".aetheus/toolchains.lock.yaml", repository.Keys);
            Assert.Contains("global.json", repository.Keys);
            Assert.Contains("package.json", repository.Keys);
            Assert.Contains("package-lock.json", repository.Keys);
            Assert.Contains("src/Toto.Api/Toto.Api.csproj", repository.Keys);
            Assert.Contains("src/Toto.Api/Program.cs", repository.Keys);
            Assert.Contains("deploy/Dockerfile", repository.Keys);
            Assert.Contains("deploy/docker-compose.yml", repository.Keys);
            Assert.Contains(".dockerignore", repository.Keys);
            Assert.Contains("**/bin", repository[".dockerignore"], StringComparison.Ordinal);
            Assert.Contains("**/obj", repository[".dockerignore"], StringComparison.Ordinal);
            Assert.Contains("USER app", repository["deploy/Dockerfile"], StringComparison.Ordinal);
            Assert.Contains(
                "COPY --from=build --chown=app:app",
                repository["deploy/Dockerfile"],
                StringComparison.Ordinal);
            Assert.Contains(
                "database:/var/lib/postgresql",
                repository["deploy/docker-compose.yml"],
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "database:/var/lib/postgresql/data",
                repository["deploy/docker-compose.yml"],
                StringComparison.Ordinal);
            Assert.Contains(
                "Aetheus__Telemetry__EnableLogs",
                repository["deploy/docker-compose.yml"],
                StringComparison.Ordinal);
            Assert.Contains(
                "Aetheus__Telemetry__TraceSampleRatio",
                repository["deploy/docker-compose.yml"],
                StringComparison.Ordinal);
            Assert.Contains(
                "app.UseStaticFiles();",
                repository["src/Toto.Api/Program.cs"],
                StringComparison.Ordinal);
            Assert.Contains(
                "createAetheusAnalytics",
                repository["src/Toto.Api/Program.cs"],
                StringComparison.Ordinal);
            Assert.Contains(
                "capturePerformance: true",
                repository["src/Toto.Api/Program.cs"],
                StringComparison.Ordinal);
            Assert.Contains(
                "captureErrors: true",
                repository["src/Toto.Api/Program.cs"],
                StringComparison.Ordinal);
            Assert.Contains(".pipeline/toto-conformance.yaml", repository.Keys);
            Assert.Contains(".pipeline/toto-multilang.yaml", repository.Keys);
            Assert.Contains("toolchains/node/angular.json", repository.Keys);
            Assert.Contains("toolchains/java/mvnw", repository.Keys);
            Assert.Contains("toolchains/python/pyproject.toml", repository.Keys);
            Assert.Contains(
                "@sha256:",
                repository[".aetheus/toolchains.lock.yaml"],
                StringComparison.Ordinal);
            Assert.Contains("aetheus.integrations.json", repository.Keys);
            Assert.Contains("Aetheus.Telemetry", repository["aetheus.integrations.json"], StringComparison.Ordinal);
            Assert.Contains("Aetheus.WebAnalytics", repository["aetheus.integrations.json"], StringComparison.Ordinal);
            Assert.Contains("deploy/scripts/generate-delivery-contract.mjs", repository.Keys);
            Assert.Contains("deploy/scripts/verify-delivery-promotion.mjs", repository.Keys);
            Assert.Contains("deploy/scripts/resolve-injected-nuget-packages.mjs", repository.Keys);
            Assert.Contains(
                "coverlet.collector",
                repository["tests/Toto.Domain.Tests/Toto.Domain.Tests.csproj"],
                StringComparison.Ordinal);
            Assert.Contains(
                "find .aetheus-injection/feed -maxdepth 1 -name '*.nupkg'",
                repository["deploy/Dockerfile"],
                StringComparison.Ordinal);
            Assert.Contains(
                "RUN --mount=type=bind,source=.aetheus-injection",
                repository["deploy/Dockerfile"],
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "COPY .aetheus-injection/aetheus-local.crt",
                repository["deploy/Dockerfile"],
                StringComparison.Ordinal);
            Assert.Contains(
                "\"jscpd\": \"5.0.12\"",
                repository["package-lock.json"],
                StringComparison.Ordinal);
            Assert.Contains(".gitleaks.toml", repository.Keys);
            Assert.Contains("useDefault = true", repository[".gitleaks.toml"], StringComparison.Ordinal);
            Assert.Contains(".trivyignore.yaml", repository.Keys);
            Assert.Contains("misconfigurations: []", repository[".trivyignore.yaml"], StringComparison.Ordinal);
        }

        Assert.Contains("version = 1", baseline["src/Toto.Api/Program.cs"], StringComparison.Ordinal);
        Assert.DoesNotContain("ADD COLUMN", baseline["src/Toto.Api/Program.cs"], StringComparison.Ordinal);
        Assert.Contains("version = 2", current["src/Toto.Api/Program.cs"], StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS priority", current["src/Toto.Api/Program.cs"], StringComparison.Ordinal);
        var qaPipeline = current[".pipeline/toto-qa.yaml"];
        Assert.Contains("/health/ready", qaPipeline, StringComparison.Ordinal);
        Assert.Contains("CURRENT_VERSION=1", qaPipeline, StringComparison.Ordinal);
        Assert.Contains("CURRENT_VERSION=2", qaPipeline, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_SCHEMA=\"$(cat .qa-current-version)\"", qaPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("EXPECTED_SCHEMA=2", qaPipeline, StringComparison.Ordinal);

        Assert.Contains("src/Toto.Domain/VulnerableTodoRules.cs", vulnerable.Keys);
        Assert.Contains("src/Toto.Domain/DangerousTodoOperations.cs", vulnerable.Keys);
        Assert.Contains("security-canaries.txt", vulnerable.Keys);
        Assert.Contains("README.md", vulnerable.Keys);
        Assert.Contains(".pipeline/toto-vulnerable-conformance.yaml", vulnerable.Keys);
        Assert.DoesNotContain(".pipeline/toto-candidate.yaml", vulnerable.Keys);
        Assert.DoesNotContain(".pipeline/toto-qa.yaml", vulnerable.Keys);
        Assert.Contains(
            ".analysis-image/aetheus-back.tar",
            vulnerable[".pipeline/toto-security.yaml"],
            StringComparison.Ordinal);
        Assert.Contains(
            ".analysis-image/source-commit",
            vulnerable[".pipeline/toto-security.yaml"],
            StringComparison.Ordinal);
        Assert.Contains(
            "npm ci --ignore-scripts --no-audit --no-fund",
            vulnerable[".pipeline/toto-quality.yaml"],
            StringComparison.Ordinal);
        Assert.DoesNotContain("__TOTO_VULNERABLE_", string.Join('\n', vulnerable.Values), StringComparison.Ordinal);
        Assert.Contains(
            string.Concat("ghp_", new string('0', 36)),
            vulnerable["security-canaries.txt"],
            StringComparison.Ordinal);
        Assert.Contains(
            string.Concat("-----BEGIN ", "RSA PRIVATE KEY-----"),
            vulnerable["security-canaries.txt"],
            StringComparison.Ordinal);
        var complexity = ComplexityAnalyzer.Analyze(
            vulnerable
                .Where(entry => entry.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(entry => (entry.Key, entry.Value)));
        Assert.True(complexity.MaxCyclomatic > 25);
        Assert.True(complexity.HighComplexityMethods >= 2);
        Assert.DoesNotContain(
            "VulnerableTodoRules",
            string.Join('\n', current.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConformanceOrchestrator_UsesExplicitIndependentBaselineBranch()
    {
        var pipelines = TotoConformanceSeeder.LoadPipelineFiles();
        var yaml = pipelines[".pipeline/toto-conformance.yaml"];

        Assert.Contains("inherit_source: false", yaml, StringComparison.Ordinal);
        Assert.Contains($"source_branch: {TotoConformanceSeeder.BaselineBranch}", yaml, StringComparison.Ordinal);
        Assert.Contains("pipeline: toto-candidate", yaml, StringComparison.Ordinal);
        Assert.Contains("pipeline: toto-promote-accept", yaml, StringComparison.Ordinal);
    }

    // BTEST-001 (STD-TESTSCOPE): the Windows smoke and image pipelines were held by assertions on the
    // literal text of their YAML (commands, names, selectors), which proved no behaviour. They are
    // replaced by what the product does with every pipeline the seeder publishes: the editor's
    // validation, template inheritance included, must accept it, or the seeded pipeline cannot be
    // saved or run. Whether the Windows scenario really builds, tests and deploys is proved by running
    // it on a Windows runner.
    public static TheoryData<string> SeededPipelineNames() => new(TotoConformanceSeeder.PipelineNames);

    [Theory]
    [MemberData(nameof(SeededPipelineNames))]
    public async Task SeededPipeline_PassesTheEditorValidationWithItsTemplates(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var organization = new Organization
        {
            Name = "Aetheus",
            Slug = "aetheus",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System).SeedAsync(ct);
        var repository = new PipelineRepository(
            db,
            TimeProvider.System,
            NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(db, TimeProvider.System),
            new PipelineRunLineageRepository(db));
        var editor = new PipelineService(
            repository, Substitute.For<IPipelineGitService>(),
            Substitute.For<IAuditService>(), NullLogger<PipelineService>.Instance,
            Substitute.For<IEntityChangeNotifier>(), Substitute.For<IHttpContextAccessor>(),
            TimeProvider.System, new PipelineTemplateResolver(repository),
            Substitute.For<IOrganizationRepository>());
        var yaml = TotoConformanceSeeder.LoadPipelineFiles()[$".pipeline/{name}.yaml"];

        var result = await editor.ValidateYamlStrictAsync(
            yaml, projectId: null, environmentId: null, projectServerId: null, organization.Id, ct);

        Assert.True(result.IsValid, string.Join(System.Environment.NewLine, result.Errors));
    }

    [Fact]
    public void CandidateAdapter_ReliesOnRestoreOperationForProductionBaselineIdentity()
    {
        var yaml = TotoConformanceSeeder.LoadPipelineFiles()[".pipeline/toto-candidate.yaml"];

        Assert.DoesNotContain("target_directory:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CONTRACT=\".pipeline-artifacts/delivery-contract.json\"",
            yaml,
            StringComparison.Ordinal);
        Assert.Contains("remove: true", yaml, StringComparison.Ordinal);
        Assert.Contains(
            "condition: \"ne(variables['DELIVERY_BASELINE_BOOTSTRAP'], 'false')\"",
            yaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "##aetheus[setvariable name=DELIVERY_BASELINE_BOOTSTRAP]true",
            yaml,
            StringComparison.Ordinal);
        Assert.Contains("depends_on: [BaselineFallback]", yaml, StringComparison.Ordinal);
        Assert.Contains("release: current-deployed", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeedAsync_IsIdempotentAndCreatesNoFabricatedExecutionEvidence()
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization
        {
            Name = "Aetheus",
            Slug = "aetheus",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        });
        db.Projects.Add(new Project
        {
            Id = 1,
            Name = TotoConformanceSeeder.LegacyProjectName,
            Description = "Legacy conformance project.",
            DefaultBranch = "main",
            Status = Aetheus.Shared.Components.Projects.ProjectStatus.Active,
            OrganizationId = 1,
            Tags = "[\"conformance\",\"toto\"]",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        });
        db.Projects.Add(new Project
        {
            Id = 2,
            Name = TotoConformanceSeeder.ProjectName,
            Description = "Legacy demo project.",
            DefaultBranch = "main",
            Status = Aetheus.Shared.Components.Projects.ProjectStatus.Active,
            OrganizationId = 1,
            Tags = "[\"demo\",\"qa\",\"toto\"]",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        });
        db.GitInternalRepos.Add(new GitInternalRepo
        {
            Id = 7,
            ProjectId = 1,
            Name = TotoConformanceSeeder.RepositoryName,
            Slug = TotoConformanceSeeder.RepositoryName,
            DefaultBranch = "main",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        });
        db.GitInternalRepos.Add(new GitInternalRepo
        {
            Id = 8,
            ProjectId = 3,
            Name = TotoConformanceSeeder.VulnerableRepositoryName,
            Slug = TotoConformanceSeeder.VulnerableRepositoryName,
            DefaultBranch = "main",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var git = Substitute.For<IGitLightService>();
        var gitCli = Substitute.For<IGitLightCliService>();
        var repository = new GitLightRepoDto
        {
            Id = 7,
            ProjectId = 1,
            Name = TotoConformanceSeeder.RepositoryName,
            Slug = TotoConformanceSeeder.RepositoryName,
            DefaultBranch = "main"
        };
        var vulnerableRepository = new GitLightRepoDto
        {
            Id = 8,
            ProjectId = 3,
            Name = TotoConformanceSeeder.VulnerableRepositoryName,
            Slug = TotoConformanceSeeder.VulnerableRepositoryName,
            DefaultBranch = "main"
        };
        git.GetRepositoriesAsync(1, Arg.Any<CancellationToken>()).Returns([repository]);
        git.GetRepositoriesAsync(3, Arg.Any<CancellationToken>()).Returns([vulnerableRepository]);
        git.BuildCloneUrl(Arg.Any<int>(), Arg.Any<string>())
            .Returns(call => $"https://aetheus.test/git/{call.ArgAt<int>(0)}/{call.ArgAt<string>(1)}.git");
        git.ResolveDiskPath(1, repository.Slug).Returns("C:\\git\\toto-conformance.git");
        git.ResolveDiskPath(3, vulnerableRepository.Slug).Returns("C:\\git\\toto-vulnerable.git");
        git.GetTagsAsync(repository.Id, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightTagDto { Name = TotoConformanceSeeder.BaselineTag, Sha = new string('a', 40) },
            new GitLightTagDto { Name = TotoConformanceSeeder.CurrentTag, Sha = new string('b', 40) }
        ]);
        git.GetBranchesAsync(repository.Id, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightBranchDto { Name = "main", IsDefault = true },
            new GitLightBranchDto { Name = TotoConformanceSeeder.BaselineBranch },
            new GitLightBranchDto { Name = TotoConformanceSeeder.DevelopBranch }
        ]);
        git.GetTagsAsync(vulnerableRepository.Id, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightTagDto { Name = TotoConformanceSeeder.VulnerableTag, Sha = new string('d', 40) }
        ]);
        git.GetBranchesAsync(vulnerableRepository.Id, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightBranchDto { Name = "main", IsDefault = true },
            new GitLightBranchDto { Name = TotoConformanceSeeder.DevelopBranch }
        ]);
        gitCli.CommitFilesAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(string RelativePath, string Content)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, new string('c', 40), null));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:ConformanceToto"] = "true",
                ["Aetheus:PublicApiBaseUrl"] = "https://aetheus.test"
            })
            .Build();
        var webAnalyticsConfiguration = Substitute.For<IAppWebAnalyticsConfigurationService>();
        var artifactStorage = Substitute.For<IArtifactStorageService>();
        webAnalyticsConfiguration.ConfigureAsync(
                Arg.Any<int>(),
                Arg.Any<ConfigureAppWebAnalyticsRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var appId = call.ArgAt<int>(0);
                var request = call.ArgAt<ConfigureAppWebAnalyticsRequest>(1);
                var app = await db.MonitoredApps.SingleAsync(
                    candidate => candidate.Id == appId,
                    TestContext.Current.CancellationToken);
                app.AnalyticsEnabled = request.Enabled;
                app.AnalyticsPublicIngestEnabled = request.PublicIngestEnabled;
                app.AnalyticsSiteId = request.SiteId;
                app.AnalyticsVaultName = $"aetheus-web-analytics-{appId}";
                app.AnalyticsPseudonymKeyVersion = 1;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                return (AppWebAnalyticsConfigurationDto?)new AppWebAnalyticsConfigurationDto
                {
                    Enabled = request.Enabled,
                    PublicIngestEnabled = request.PublicIngestEnabled,
                    SiteId = request.SiteId,
                    AllowedOrigins = request.AllowedOrigins,
                    StorageBudgetBytes = request.StorageBudgetBytes,
                    PseudonymKeyVersion = 1
                };
            });
        var sut = new TotoConformanceSeeder(
            db, git, gitCli, artifactStorage, webAnalyticsConfiguration, configuration, TimeProvider.System,
            NullLogger<TotoConformanceSeeder>.Instance);

        await sut.SeedAsync(TestContext.Current.CancellationToken);
        var staleMonitoredApp = await db.MonitoredApps
            .SingleAsync(
                app => app.ProjectId == 1 && app.Name == TotoConformanceSeeder.MonitoredAppName,
                TestContext.Current.CancellationToken);
        staleMonitoredApp.ServerId = 99;
        staleMonitoredApp.ProbeUrl = "http://127.0.0.1:10090/api/todos";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vulnerableProjectBeforeReseed = await db.Projects
            .SingleAsync(
                project => project.Name == TotoConformanceSeeder.VulnerableProjectName,
                TestContext.Current.CancellationToken);
        db.Pipelines.AddRange(
            new Pipeline
            {
                Name = "toto-qa",
                Description = "Toto Vulnerable candidate pipeline: toto-qa.",
                ProjectId = vulnerableProjectBeforeReseed.Id,
                CreatedByUsername = "admin",
                CreatedAt = DateTime.UnixEpoch,
                UpdatedAt = DateTime.UnixEpoch
            },
            new Pipeline
            {
                Name = "toto-candidate",
                Description = "Toto Vulnerable candidate pipeline: toto-candidate.",
                ProjectId = vulnerableProjectBeforeReseed.Id,
                CreatedByUsername = "admin",
                CreatedAt = DateTime.UnixEpoch,
                UpdatedAt = DateTime.UnixEpoch
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await sut.SeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, await db.Projects.CountAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            await db.Projects.ToListAsync(TestContext.Current.CancellationToken),
            project => project.Name == TotoConformanceSeeder.ProjectName);
        Assert.Contains(
            await db.Projects.ToListAsync(TestContext.Current.CancellationToken),
            project => project.Name == TotoConformanceSeeder.VulnerableProjectName);
        Assert.Contains(
            await db.Projects.ToListAsync(TestContext.Current.CancellationToken),
            project => project.Name == TotoConformanceSeeder.DemoProjectName
                       && project.Tags.Contains("\"demo\"", StringComparison.Ordinal));
        Assert.Equal(3, await db.Environments.CountAsync(TestContext.Current.CancellationToken));
        var monitoredApp = await db.MonitoredApps.SingleAsync(
            app => app.ProjectId == 1 && app.Name == TotoConformanceSeeder.MonitoredAppName,
            TestContext.Current.CancellationToken);
        Assert.Null(monitoredApp.ServerId);
        Assert.Null(monitoredApp.ProbeUrl);
        Assert.True(monitoredApp.AnalyticsEnabled);
        Assert.True(monitoredApp.AnalyticsPublicIngestEnabled);
        await webAnalyticsConfiguration.Received(1).ConfigureAsync(
            monitoredApp.Id,
            Arg.Is<ConfigureAppWebAnalyticsRequest>(request =>
                request.SiteId == "toto-accept"
                && request.AllowedOrigins.SequenceEqual(new[] { "https://localhost:11443" })),
            Arg.Any<CancellationToken>());
        var conformanceProject = await db.Projects
            .SingleAsync(
                project => project.Name == TotoConformanceSeeder.ProjectName,
                TestContext.Current.CancellationToken);
        var conformanceEnvironmentIds = await db.Environments
            .Where(environment => environment.ProjectId == conformanceProject.Id)
            .Select(environment => environment.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await db.EnvironmentServers
            .Where(link => conformanceEnvironmentIds.Contains(link.EnvironmentId))
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.VariableLibraries.CountAsync(TestContext.Current.CancellationToken));
        var variables = await db.VariableLibraries
            .Include(candidate => candidate.Entries)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(variables, library =>
            Assert.Contains(library.Entries, entry =>
                entry.Key == "AETHEUS_PACKAGE_BASE_URL" && entry.Value == "https://aetheus.test"));
        var vaults = await db.Vaults
            .Include(candidate => candidate.Secrets)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, vaults.Count);
        Assert.All(vaults, vault =>
        {
            Assert.Equal(TotoConformanceSeeder.RegistryVaultName, vault.Name);
            Assert.Empty(vault.Secrets);
        });
        Assert.Equal(
            TotoConformanceSeeder.PipelineNames.Length + TotoConformanceSeeder.VulnerablePipelineNames.Length,
            await db.Pipelines.CountAsync(TestContext.Current.CancellationToken));
        var vulnerableProject = await db.Projects
            .SingleAsync(
                project => project.Name == TotoConformanceSeeder.VulnerableProjectName,
                TestContext.Current.CancellationToken);
        var vulnerableEnvironments = await db.Environments
            .Where(environment => environment.ProjectId == vulnerableProject.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Collection(
            vulnerableEnvironments,
            environment =>
            {
                Assert.Equal("qa", environment.Name);
                Assert.Equal(Aetheus.Shared.Components.Environments.EnvironmentType.Testing, environment.Type);
                Assert.True(environment.DastEnabled);
                Assert.True(environment.DastIsEphemeral);
            });
        Assert.Equal(
            TotoConformanceSeeder.VulnerablePipelineNames.Order(StringComparer.Ordinal).ToArray(),
            await db.Pipelines
                .Where(pipeline => pipeline.ProjectId == vulnerableProject.Id)
                .OrderBy(pipeline => pipeline.Name)
                .Select(pipeline => pipeline.Name)
                .ToArrayAsync(TestContext.Current.CancellationToken),
            StringComparer.Ordinal);
        Assert.Empty(await db.PipelineRuns.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Releases.ToListAsync(TestContext.Current.CancellationToken));
        await git.Received(4).EnsureRepositoryInitializedAsync(
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
        await gitCli.Received(6).CommitFilesAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<(string RelativePath, string Content)>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, ReleaseStatus.Superseded)]
    [InlineData(128, ReleaseStatus.Deployed)]
    public async Task ReconcileMissingConformanceDeploymentAsync_OnlyInvalidatesMissingRetainedBytes(
        long retainedSize,
        ReleaseStatus expectedStatus)
    {
        await using var db = CreateDb();
        var project = new Project
        {
            Id = 1,
            Name = TotoConformanceSeeder.ProjectName,
            OrganizationId = 1,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
        var pipeline = new Pipeline
        {
            Id = 1,
            Name = "toto-ci",
            ProjectId = project.Id,
            Project = project,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = pipeline.Id,
            Pipeline = pipeline,
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UnixEpoch,
            CompletedAt = DateTime.UnixEpoch
        };
        var artifact = new PipelineArtifact
        {
            Id = 1,
            PipelineRunId = run.Id,
            PipelineId = pipeline.Id,
            ProjectId = project.Id,
            PipelineRun = run,
            Pipeline = pipeline,
            Project = project,
            Name = "Package-artifacts",
            FilePath = "1/1/1/package.zip",
            SizeBytes = 128,
            Sha256 = new string('a', 64),
            CreatedAt = DateTime.UnixEpoch,
            RetentionExpiresAt = DateTime.MaxValue
        };
        var release = new Release
        {
            Id = 1,
            ProjectId = project.Id,
            Project = project,
            Version = "c-test",
            BranchName = "develop",
            Status = ReleaseStatus.Deployed,
            DetectedAt = DateTime.UnixEpoch,
            PipelineRunId = run.Id,
            PipelineRun = run,
            Artifacts = [artifact]
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var storage = Substitute.For<IArtifactStorageService>();
        storage.GetArtifactSize(artifact.FilePath).Returns(retainedSize);
        var sut = new TotoConformanceSeeder(
            db,
            Substitute.For<IGitLightService>(),
            Substitute.For<IGitLightCliService>(),
            storage,
            Substitute.For<IAppWebAnalyticsConfigurationService>(),
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<TotoConformanceSeeder>.Instance);

        await sut.ReconcileMissingConformanceDeploymentAsync(
            project.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, release.Status);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
