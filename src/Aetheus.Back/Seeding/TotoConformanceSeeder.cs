// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Seeding;

/// <summary>
/// Opt-in production-safe seed for the real Toto pipeline conformance laboratory. Unlike demo
/// seeding, it creates no fabricated runs, reports, artifacts or releases. It provisions an
/// internal Git repository with deterministic V-1/V revisions and the pipelines that exercise them.
/// </summary>
public sealed class TotoConformanceSeeder(
    AppDbContext db,
    IGitLightService git,
    IGitLightCliService gitCli,
    IArtifactStorageService artifactStorage,
    IAppWebAnalyticsConfigurationService webAnalyticsConfiguration,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<TotoConformanceSeeder> logger)
{
    internal const string ProjectName = "Toto";
    internal const string LegacyProjectName = "Toto Pipeline Conformance";
    internal const string DemoProjectName = "Toto Demo";
    internal const string RepositoryName = "toto-conformance";
    internal const string VulnerableProjectName = "Toto Vulnerable";
    internal const string VulnerableRepositoryName = "toto-vulnerable";
    internal const string LibraryName = "toto-observability";
    internal const string RegistryVaultName = "toto-package-registry";
    internal const string MonitoredAppName = "toto-accept";
    private readonly TotoConformanceRepositorySeeder repositorySeeder = new(git, gitCli);

    internal static readonly string[] PipelineNames =
    [
        "toto-ci-light",
        "toto-ci-light-adapter",
        "toto-windows-smoke",
        "toto-windows-image",
        "toto-ci",
        "toto-multilang",
        "toto-quality",
        "toto-security",
        "toto-qa",
        "toto-candidate",
        "toto-candidate-full",
        "toto-baseline-candidate",
        "toto-promote-accept",
        "toto-deploy-accept-adapter",
        "toto-verify-accept",
        "toto-conformance",
        "toto-conformance-cancellable",
        "toto-conformance-injection",
        "toto-conformance-negative",
        "toto-conformance-stale"
    ];

    internal static readonly string[] VulnerablePipelineNames =
    [
        "toto-ci",
        "toto-quality",
        "toto-security",
        "toto-vulnerable-conformance"
    ];

    private static readonly string[] RetiredVulnerablePipelineNames =
    [
        "toto-qa",
        "toto-candidate"
    ];

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!configuration.GetValue("Seed:ConformanceToto", false))
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var organizationId = await db.Organizations
            .Where(organization => organization.Slug == "aetheus")
            .Select(organization => organization.Id)
            .SingleAsync(ct).ConfigureAwait(false);

        var candidates = await db.Projects
            .Where(candidate =>
                candidate.Name == ProjectName
                || candidate.Name == LegacyProjectName
                || candidate.Name == DemoProjectName)
            .ToListAsync(ct).ConfigureAwait(false);
        var project = candidates.SingleOrDefault(candidate => candidate.Name == LegacyProjectName);
        var namedToto = candidates.SingleOrDefault(candidate => candidate.Name == ProjectName);
        if (namedToto is not null && !HasTag(namedToto, "conformance"))
        {
            if (!HasTag(namedToto, "demo"))
                throw new InvalidOperationException(
                    "A project named 'Toto' already exists but is neither the conformance project nor demo data.");
            if (candidates.Any(candidate => candidate.Name == DemoProjectName))
                throw new InvalidOperationException(
                    "Both legacy 'Toto' demo data and 'Toto Demo' exist; refusing an ambiguous rename.");
            namedToto.Name = DemoProjectName;
            namedToto.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            namedToto = null;
        }
        if (project is not null && namedToto is not null)
            throw new InvalidOperationException(
                "Both legacy and current Toto conformance projects exist; refusing to merge execution evidence.");
        project ??= namedToto;
        if (project is null)
        {
            project = new Project
            {
                Name = ProjectName,
                Description = "Isolated real project used to validate Aetheus pipelines, promotion and rollback.",
                DefaultBranch = "main",
                Status = ProjectStatus.Active,
                Tags = "[\"conformance\",\"toto\"]",
                OrganizationId = organizationId,
                ReleaseNumberingPattern = "c-$(BUILD_SOURCEVERSION)",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        project.Name = ProjectName;
        project.Description = "Healthy isolated project used to validate Aetheus pipelines, promotion and rollback.";
        project.Tags = "[\"conformance\",\"toto\",\"healthy\"]";

        var repositories = await git.GetRepositoriesAsync(project.Id, ct).ConfigureAwait(false);
        var repository = repositories.SingleOrDefault(candidate =>
            string.Equals(candidate.Slug, RepositoryName, StringComparison.OrdinalIgnoreCase))
            ?? await git.CreateRepositoryAsync(new CreateGitLightRepoRequest
            {
                ProjectId = project.Id,
                Name = RepositoryName,
                Description = "Deterministic V-1/V source for the Toto pipeline conformance laboratory.",
                DefaultBranch = "main"
            }, ct).ConfigureAwait(false);

        project.RepositoryUrl = git.BuildCloneUrl(project.Id, repository.Slug);
        project.DefaultBranch = repository.DefaultBranch;
        project.UpdatedAt = now;

        var pipelineFiles = LoadPipelineFiles();
        await repositorySeeder.SeedAsync(project.Id, repository, pipelineFiles, ct).ConfigureAwait(false);
        await ReconcileMissingConformanceDeploymentAsync(project.Id, ct).ConfigureAwait(false);
        await SeedEnvironmentsAsync(project.Id, includeAccept: true, now, ct).ConfigureAwait(false);
        await SeedMonitoredAppAsync(project.Id, now, ct).ConfigureAwait(false);
        await SeedVariablesAsync(project.Id, now, ct).ConfigureAwait(false);
        await SeedRegistryVaultAsync(project.Id, now, ct).ConfigureAwait(false);
        await SeedPipelinesAsync(
                project.Id, repository.Id, repository.DefaultBranch, pipelineFiles, PipelineNames,
                "Toto conformance pipeline", now, ct)
            .ConfigureAwait(false);

        await MarkRepositorySeededAsync(repository.Id, now, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Toto conformance seed ready: project {ProjectId}, repository {RepositoryId}, baseline {BaselineBranch}.",
            project.Id, repository.Id, TotoConformanceContent.BaselineBranch);

        await SeedVulnerableProjectAsync(organizationId, now, ct).ConfigureAwait(false);
    }

    internal async Task ReconcileMissingConformanceDeploymentAsync(int projectId, CancellationToken ct)
    {
        var deployedRelease = await db.Releases
            .Include(release => release.Artifacts)
            .SingleOrDefaultAsync(
                release => release.ProjectId == projectId && release.Status == ReleaseStatus.Deployed,
                ct)
            .ConfigureAwait(false);
        if (deployedRelease is null)
            return;

        var retainedArtifact = deployedRelease.Artifacts
            .OrderByDescending(artifact => artifact.CreatedAt)
            .FirstOrDefault();
        var retainedSize = 0L;
        if (retainedArtifact is not null)
        {
            try
            {
                retainedSize = artifactStorage.GetArtifactSize(retainedArtifact.FilePath);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogWarning(
                    exception,
                    "Toto conformance release {ReleaseId} references an invalid artifact path.",
                    deployedRelease.Id);
            }
        }

        if (retainedArtifact is not null
            && retainedArtifact.SizeBytes > 0
            && retainedSize == retainedArtifact.SizeBytes)
            return;

        deployedRelease.Status = ReleaseStatus.Superseded;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogWarning(
            "Toto conformance release {ReleaseId} was superseded because retained artifact {ArtifactId} is missing or incomplete; the next candidate run will use explicit bootstrap mode.",
            deployedRelease.Id,
            retainedArtifact?.Id);
    }

    private async Task SeedVulnerableProjectAsync(
        int organizationId,
        DateTime now,
        CancellationToken ct)
    {
        var project = await db.Projects
            .SingleOrDefaultAsync(candidate => candidate.Name == VulnerableProjectName, ct)
            .ConfigureAwait(false);
        if (project is null)
        {
            project = new Project
            {
                Name = VulnerableProjectName,
                DefaultBranch = "main",
                Status = ProjectStatus.Active,
                OrganizationId = organizationId,
                ReleaseNumberingPattern = "never-release-$(BUILD_SOURCEVERSION)",
                CreatedAt = now
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        project.Description =
            "Isolated candidate project containing deterministic quality and security defects.";
        project.Tags = "[\"conformance\",\"toto\",\"vulnerable\",\"negative-gates\"]";
        project.UpdatedAt = now;

        var repositories = await git.GetRepositoriesAsync(project.Id, ct).ConfigureAwait(false);
        var repository = repositories.SingleOrDefault(candidate =>
            string.Equals(candidate.Slug, VulnerableRepositoryName, StringComparison.OrdinalIgnoreCase))
            ?? await git.CreateRepositoryAsync(new CreateGitLightRepoRequest
            {
                ProjectId = project.Id,
                Name = VulnerableRepositoryName,
                Description = "Deterministically vulnerable source for the Toto candidate laboratory.",
                DefaultBranch = "main"
            }, ct).ConfigureAwait(false);

        project.RepositoryUrl = git.BuildCloneUrl(project.Id, repository.Slug);
        project.DefaultBranch = repository.DefaultBranch;
        project.UpdatedAt = now;

        await RemoveRetiredVulnerablePipelinesAsync(project.Id, ct).ConfigureAwait(false);
        var pipelineFiles = LoadVulnerablePipelineFiles();
        await repositorySeeder.SeedVulnerableAsync(project.Id, repository, pipelineFiles, ct).ConfigureAwait(false);
        await SeedEnvironmentsAsync(project.Id, includeAccept: false, now, ct).ConfigureAwait(false);
        await SeedVariablesAsync(project.Id, now, ct).ConfigureAwait(false);
        await SeedRegistryVaultAsync(project.Id, now, ct).ConfigureAwait(false);
        await SeedPipelinesAsync(
                project.Id, repository.Id, repository.DefaultBranch, pipelineFiles, VulnerablePipelineNames,
                "Toto Vulnerable negative conformance pipeline", now, ct)
            .ConfigureAwait(false);

        await MarkRepositorySeededAsync(repository.Id, now, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Toto Vulnerable seed ready: project {ProjectId}, repository {RepositoryId}, tag {Tag}.",
            project.Id, repository.Id, TotoConformanceContent.VulnerableTag);
    }

    private async Task MarkRepositorySeededAsync(int repositoryId, DateTime now, CancellationToken ct)
    {
        var repository = await db.GitInternalRepos
            .SingleAsync(candidate => candidate.Id == repositoryId, ct).ConfigureAwait(false);
        repository.IsEmpty = false;
        repository.UpdatedAt = now;
        repository.LastPushAt ??= now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RemoveRetiredVulnerablePipelinesAsync(int projectId, CancellationToken ct)
    {
        const string seededDescriptionPrefix = "Toto Vulnerable candidate pipeline:";
        var retired = await db.Pipelines
            .Where(pipeline =>
                pipeline.ProjectId == projectId
                && RetiredVulnerablePipelineNames.Contains(pipeline.Name)
                && pipeline.CreatedByUsername == "admin"
                && pipeline.Description.StartsWith(seededDescriptionPrefix)
                && !db.PipelineRuns.Any(run => run.PipelineId == pipeline.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        if (retired.Count == 0)
            return;

        db.Pipelines.RemoveRange(retired);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation(
            "Removed {Count} retired execution-free Toto Vulnerable pipelines.",
            retired.Count);
    }

    private async Task SeedEnvironmentsAsync(
        int projectId,
        bool includeAccept,
        DateTime now,
        CancellationToken ct)
    {
        var environments = includeAccept
            ? new[] { ("qa", EnvironmentType.Testing), ("accept", EnvironmentType.Staging) }
            : [("qa", EnvironmentType.Testing)];
        foreach (var (name, type) in environments)
        {
            var environment = await db.Environments.SingleOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Name == name, ct).ConfigureAwait(false);
            environment ??= new Data.Entities.Environment
            {
                Name = name,
                ProjectId = projectId,
                CreatedAt = now
            };
            environment.Description = $"Isolated Toto conformance {name} environment.";
            environment.Type = type;
            environment.RequireApproval = false;
            environment.DastEnabled = name == "qa";
            environment.DastIsEphemeral = name == "qa";
            environment.DastContainsRealData = false;
            environment.DastAllowedHosts = name == "qa" ? "127.0.0.1" : string.Empty;
            environment.UpdatedAt = now;
            if (environment.Id == 0)
                db.Environments.Add(environment);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SeedVariablesAsync(int projectId, DateTime now, CancellationToken ct)
    {
        var library = await db.VariableLibraries
            .Include(candidate => candidate.Entries)
            .SingleOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Name == LibraryName, ct)
            .ConfigureAwait(false);
        if (library is null)
        {
            library = new VariableLibrary
            {
                Name = LibraryName,
                Description = "Non-secret ports and identities for the isolated Toto laboratory.",
                ProjectId = projectId,
                CreatedAt = now
            };
            db.VariableLibraries.Add(library);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TOTO_QA_PORT"] = "10091",
            ["TOTO_ACCEPT_PORT"] = "10090",
            ["TOTO_BASELINE_BRANCH"] = TotoConformanceContent.BaselineBranch
        };
        var publicApiBaseUrl = configuration["Aetheus:PublicApiBaseUrl"]?.TrimEnd('/');
        if (Uri.TryCreate(publicApiBaseUrl, UriKind.Absolute, out var publicApiUri)
            && publicApiUri.Scheme == Uri.UriSchemeHttps)
            values["AETHEUS_PACKAGE_BASE_URL"] = publicApiBaseUrl!;

        foreach (var (key, value) in values)
        {
            var entry = library.Entries.SingleOrDefault(candidate => candidate.Key == key);
            if (entry is null)
                library.Entries.Add(new VariableLibraryEntry { Key = key, Value = value });
            else
                entry.Value = value;
        }
        library.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SeedMonitoredAppAsync(
        int projectId,
        DateTime now,
        CancellationToken ct)
    {
        var environmentId = await db.Environments
            .Where(environment => environment.ProjectId == projectId && environment.Name == "accept")
            .Select(environment => environment.Id)
            .SingleAsync(ct).ConfigureAwait(false);
        var app = await db.MonitoredApps
            .SingleOrDefaultAsync(
                candidate => candidate.ProjectId == projectId && candidate.Name == MonitoredAppName,
                ct).ConfigureAwait(false);
        if (app is null)
        {
            app = new MonitoredApp
            {
                ProjectId = projectId,
                Name = MonitoredAppName,
                CreatedAt = now
            };
            db.MonitoredApps.Add(app);
        }
        app.EnvironmentId = environmentId;
        app.ServerId = null;
        app.ProbeUrl = null;
        app.Enabled = true;
        app.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (!app.AnalyticsEnabled
            || !app.AnalyticsPublicIngestEnabled
            || string.IsNullOrWhiteSpace(app.AnalyticsVaultName))
        {
            var origin = configuration["TotoConformance:AnalyticsOrigin"]
                ?? "https://localhost:11443";
            var configured = await webAnalyticsConfiguration.ConfigureAsync(
                app.Id,
                new ConfigureAppWebAnalyticsRequest
                {
                    Enabled = true,
                    PublicIngestEnabled = true,
                    SiteId = "toto-accept",
                    AllowedOrigins = [origin],
                    StorageBudgetBytes = AppMonitoringDefaults.DefaultAnalyticsStorageBudgetBytes
                },
                ct).ConfigureAwait(false);
            if (configured is null)
                throw new InvalidOperationException("Could not configure Toto web analytics.");
        }
    }

    private async Task SeedRegistryVaultAsync(int projectId, DateTime now, CancellationToken ct)
    {
        var vault = await db.Vaults.SingleOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Name == RegistryVaultName,
            ct).ConfigureAwait(false);
        if (vault is null)
        {
            vault = new Vault
            {
                Name = RegistryVaultName,
                ProjectId = projectId,
                CreatedAt = now
            };
            db.Vaults.Add(vault);
        }
        vault.Description =
            "Operator-managed AETHEUS_PACKAGE_TOKEN for published-package injection; the seed never creates secrets.";
        vault.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SeedPipelinesAsync(
        int projectId,
        int repositoryId,
        string defaultBranch,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyCollection<string> pipelineNames,
        string descriptionPrefix,
        DateTime now,
        CancellationToken ct)
    {
        var definitions = files.Values
            .Select(yaml => YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml))
            .ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
        var existing = await db.Pipelines
            .Where(candidate => candidate.ProjectId == projectId && pipelineNames.Contains(candidate.Name))
            .ToDictionaryAsync(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        foreach (var name in pipelineNames)
        {
            var definition = definitions[name];
            var yaml = files[$".pipeline/{name}.yaml"];
            if (!existing.TryGetValue(name, out var pipeline))
            {
                pipeline = new Pipeline
                {
                    Name = name,
                    ProjectId = projectId,
                    CreatedByUsername = "admin",
                    CreatedAt = now
                };
                db.Pipelines.Add(pipeline);
            }
            pipeline.Description = $"{descriptionPrefix}: {name}.";
            pipeline.YamlDefinition = yaml;
            pipeline.TriggerType = definition.Trigger.ToLowerInvariant() switch
            {
                "webhook" => PipelineTriggerType.Webhook,
                "schedule" => PipelineTriggerType.Schedule,
                _ => PipelineTriggerType.Manual
            };
            pipeline.SourceRepositoryId = repositoryId;
            pipeline.SourceBranch = defaultBranch;
            pipeline.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    internal static IReadOnlyDictionary<string, string> LoadPipelineFiles() =>
        TotoConformanceContent.LoadResources($"{TotoConformanceContent.ResourceRoot}Pipelines/")
            .Where(entry => PipelineNames.Contains(Path.GetFileNameWithoutExtension(entry.Key), StringComparer.OrdinalIgnoreCase))
            .ToDictionary(entry => $".pipeline/{Path.GetFileName(entry.Key)}", entry => entry.Value, StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> LoadVulnerablePipelineFiles() =>
        TotoConformanceContent.LoadResources($"{TotoConformanceContent.ResourceRoot}Pipelines/")
            .Where(entry => VulnerablePipelineNames.Contains(
                Path.GetFileNameWithoutExtension(entry.Key), StringComparer.OrdinalIgnoreCase))
            .ToDictionary(
                entry => $".pipeline/{Path.GetFileName(entry.Key)}",
                entry => entry.Value,
                StringComparer.Ordinal);

    private static bool HasTag(Project project, string tag) =>
        project.Tags.Contains($"\"{tag}\"", StringComparison.OrdinalIgnoreCase);

}
