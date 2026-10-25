// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Seeding;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.DataSeed;

public sealed class DeliveryPipelineTemplateSeederTests
{
    [Fact]
    public async Task SeedAsync_IsIdempotentAndPublishesPinnedImmutableTemplates()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        db.Organizations.Add(new Organization
        {
            Name = "Aetheus",
            Slug = "aetheus",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var clock = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero));
        var seeder = new DeliveryPipelineTemplateSeeder(db, clock);

        await seeder.SeedAsync(TestContext.Current.CancellationToken);
        await seeder.SeedAsync(TestContext.Current.CancellationToken);

        var templates = await db.PipelineTemplates
            .Include(template => template.Versions)
            .OrderBy(template => template.Name)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryTemplateCatalog.Definitions.Count, templates.Count);
        Assert.All(templates, template =>
            Assert.All(template.Versions, version =>
                Assert.Contains("stages:", version.YamlContent, StringComparison.Ordinal)));
        Assert.Contains(templates, template => template.Name == "application-light");
        Assert.Contains(templates, template => template.Name == "application-ci");
        Assert.Contains(templates, template => template.Name == "application-quality");
        Assert.Contains(templates, template => template.Name == "application-security");
        Assert.Contains(templates, template => template.Name == "application-qa");
        Assert.Contains(templates, template => template.Name == "application-nightly");
        Assert.Contains(templates, template => template.Name == "application-release-fast");
        Assert.Contains(templates, template => template.Name == "publish-observability-packages");
        Assert.DoesNotContain(templates, template => template.Name.Contains("aetheus", StringComparison.OrdinalIgnoreCase));
        // Every published version has its own shipped template file.
        Assert.All(templates, template =>
        {
            var shippedFiles = DeliveryTemplateCatalog.Definitions[template.Name].Resources.Length;
            Assert.Equal(shippedFiles, template.LatestVersion);
            Assert.Equal(Enumerable.Range(1, shippedFiles), template.Versions.Select(version => version.Version).Order());
        });
        var candidate = Assert.Single(templates, template => template.Name == "application-candidate");
        Assert.Equal(3, candidate.LatestVersion);
        Assert.Contains("R2-1", candidate.Versions.Single(version => version.Version == 3).ChangelogEntry,
            StringComparison.Ordinal);
        Assert.Contains("R2-121", candidate.Versions.Single(version => version.Version == 2).ChangelogEntry,
            StringComparison.Ordinal);
        Assert.Contains(templates, template => template.Name == "application-qa-extended");
        Assert.Contains(templates, template => template.Name == "application-security-history");
        var light = Assert.Single(templates, template => template.Name == "application-light");
        Assert.Equal(1, light.LatestVersion);
        Assert.Equal(1, Assert.Single(light.Versions).Version);
        // The blue-green cutover's v1 is the content last published as v6 (confirmation window); v2 drops the
        // required `revision` run parameter (R-25) and v1 stays published unchanged for pinned consumers.
        var blueGreen = Assert.Single(templates, template => template.Name == "host-bluegreen-deploy");
        Assert.Equal(2, blueGreen.LatestVersion);
        var blueGreenV1 = blueGreen.Versions.Single(version => version.Version == 1);
        var blueGreenV2 = blueGreen.Versions.Single(version => version.Version == 2);
        Assert.Contains("confirm_minutes: \"$(BG_CONFIRM_MINUTES)\"", blueGreenV1.YamlContent, StringComparison.Ordinal);
        Assert.Contains("confirm_minutes: \"$(BG_CONFIRM_MINUTES)\"", blueGreenV2.YamlContent, StringComparison.Ordinal);
        Assert.Contains("R-25", blueGreenV2.ChangelogEntry, StringComparison.Ordinal);
        // A fresh database has nothing to renumber; the reset is marked done all the same.
        Assert.True(await db.AppSettings.AnyAsync(setting => setting.Key == DeliveryTemplateVersionReset.MarkerKey,
            TestContext.Current.CancellationToken));
        Assert.True(await db.AppSettings.AnyAsync(setting => setting.Key == DeliveryTemplateVersionReset.FirstResetMarkerKey,
            TestContext.Current.CancellationToken));
        // "Revenir à N-1" is its own template: nothing is built or migrated on the way back.
        var revert = Assert.Single(templates, template => template.Name == "host-bluegreen-revert");
        Assert.Contains("type: bluegreen-revert", Assert.Single(revert.Versions).YamlContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeedAsync_RejectsUnknownImmutableVersionMutation()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        db.Organizations.Add(new Organization
        {
            Name = "Aetheus",
            Slug = "aetheus",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var organizationId = await db.Organizations
            .Select(organization => organization.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        // A lone v1 that differs is not something the reset renumbers: immutability refuses it.
        db.PipelineTemplates.Add(CreateHistoricalCandidate(organizationId, "stages: []", versions: 1));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var seeder = new DeliveryPipelineTemplateSeeder(db, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.SeedAsync(TestContext.Current.CancellationToken));
        Assert.Contains("differs from its immutable published version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateTemplate_UsesProductionBaselineAndBuildOnceReleaseContract()
    {
        // The candidate v1 content matches its pinned definition.
        var versionOne = DeliveryTemplateCatalog.ReadResource("application-candidate-v1.yaml");

        Assert.Contains("release: previous-deployed", versionOne, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionTemplate_RequiresAnExplicitImmutableCandidateVersion()
    {
        // Verify the published v1 content hash.
        var versionOne = DeliveryTemplateCatalog.ReadResource("application-promotion-v1.yaml");

        Assert.Contains("required: true", versionOne, StringComparison.Ordinal);
        Assert.DoesNotContain("default: \"c-$(BUILD_SOURCEVERSION)\"", versionOne, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericPipelineTemplates_TakeProjectCommandsWithoutAetheusImplementationDetails()
    {
        var yaml = DeliveryTemplateCatalog.ReadResource("generic/application-nightly.yaml");
        var packages = DeliveryTemplateCatalog.ReadResource(
            "generic/publish-observability-packages.yaml");

        Assert.Contains("name: application-nightly-template", yaml, StringComparison.Ordinal);
        Assert.Contains("schedule: \"0 0 2 * * *\"", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_CI_PIPELINE", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_QUALITY_PIPELINE", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_SECURITY_PIPELINE", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CANDIDATE", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("type: release", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_PUBLISH_PACKAGES_COMMANDS", packages, StringComparison.Ordinal);
        Assert.DoesNotContain(".pipeline/scripts/", packages, StringComparison.Ordinal);
        Assert.DoesNotContain("Aetheus", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Aetheus", packages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenericPipelineTemplates_RequireNoProjectFileAndContainNoAetheusProductPaths()
    {
        var genericResources = DeliveryTemplateCatalog.Definitions.Values
            .SelectMany(definition => definition.Resources)
            .Where(resource => resource.StartsWith("generic/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(genericResources);
        foreach (var resource in genericResources)
        {
            var yaml = DeliveryTemplateCatalog.ReadResource(resource);
            var definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
            var errors = new List<string>();
            var warnings = new List<string>();
            Assert.NotNull(definition);
            PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, warnings);
            PipelineDefinitionValidator.ValidateStages(definition, errors, warnings);
            Assert.Empty(errors);
            Assert.NotEmpty(definition.Stages);
            // The platform's own interface (its output-variable marker, its checkout-depth variable)
            // is not an implementation detail of the Aetheus product; anything else named Aetheus is.
            var withoutPlatformInterface = yaml
                .Replace("##aetheus[setvariable", string.Empty, StringComparison.Ordinal)
                // ADR-054: a template reaches the platform's functions through `type: aetheus`, the step
                // type the platform names itself; it is the interface, not the product.
                .Replace("type: aetheus", string.Empty, StringComparison.Ordinal)
                .Replace("AETHEUS_GIT_HISTORY_DEPTH", string.Empty, StringComparison.Ordinal)
                // ADR-057: the name of the variable the platform hands a step its rendered environment file in
                // (`env_template`): the platform's own interface, like the two above, not the product.
                .Replace("AETHEUS_ENV_FILE_B64", string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("Aetheus", withoutPlatformInterface, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sonytumen.com", yaml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/var/lib/", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("src/", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("tests/", yaml, StringComparison.Ordinal);
        }

        // ADR-050, recette R3-64: no template calls a script of the project's repository; the
        // project's commands are declared in its pipeline (GenericTemplatesWithoutProjectFilesTests).
        Assert.All(genericResources, resource => Assert.DoesNotContain(".pipeline/scripts/",
            DeliveryTemplateCatalog.ReadResource(resource), StringComparison.Ordinal));
    }

    private static PipelineYamlDefinition Shipped(string resource) =>
        YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryTemplateCatalog.ReadResource(resource))!;

    private static IEnumerable<string> GenericResourcesAndCandidateV2() =>
        DeliveryTemplateCatalog.Definitions.Values
            .SelectMany(definition => definition.Resources)
            .Where(resource => resource.StartsWith("generic/", StringComparison.Ordinal))
            .Append("application-candidate-v2.yaml");

    /// <summary>Steps within a stage execute concurrently (ADR-012). Keep artifact restoration and
    /// release publication isolated from steps that depend on their effects.</summary>
    [Fact]
    public void GenericTemplates_NeverShareAStageBetweenARestoreOrReleaseAndAnotherStep()
    {
        foreach (var resource in GenericResourcesAndCandidateV2())
            foreach (var stage in YamlParsingHelper.FlattenJobs(Shipped(resource)))
            {
                var types = stage.Steps.Select(step => step.Type ?? "shell").Distinct().ToList();
                if (types.Contains("restore-artifacts") || types.Contains("release"))
                    Assert.True(types.Count == 1, $"{resource} stage '{stage.Name}' mixes {string.Join(", ", types)}.");
            }
    }

    /// <summary>Quality and security templates publish grades with supported rules and place each
    /// gate after its producers.</summary>
    [Theory]
    [InlineData("generic/application-quality.yaml", "quality")]
    [InlineData("generic/application-security.yaml", "security")]
    [InlineData("generic/application-security-history.yaml", "security")]
    [InlineData("generic/application-security-external.yaml", "security")]
    public void GenericAnalysisTemplates_GradeAfterTheirProducersWithAcceptedRules(string resource, string scope)
    {
        var definition = Shipped(resource);
        var stages = YamlParsingHelper.FlattenJobs(definition);
        var gate = Assert.Single(stages.SelectMany(stage => stage.Steps), step => step.Type == "analysis-gate");

        Assert.Equal(scope, gate.AnalysisScope);
        Assert.NotNull(gate.AnalysisGrading);
        Assert.Empty(PipelineAnalysisGradingValidator.Validate(scope, gate.AnalysisGrading));
        Assert.All(gate.AnalysisGrading.Rules, rule => Assert.Null(rule.FindingScore));
        Assert.NotEmpty(gate.AnalysisGrading.RequiredDomains);
        Assert.Contains(stages.SelectMany(stage => stage.Steps), step => step.Type == "scanner");
        Assert.Empty(PipelineAnalysisGateOrderingValidator.Validate(stages));
    }

    /// <summary> The template the setup wizard gives an external repository is published like the
    /// other security template, and needs nothing in that repository: only platform scanners, none
    /// of which the readiness check finds a repository file for. Gitleaks included: its configuration
    /// is optional since recette R3-63 (built-in rules when .gitleaks.toml is absent). </summary>
    [Fact]
    public void ExternalSecurityTemplate_IsPublished_AndNeedsNoFileInTheRepository()
    {
        var (_, category, resources) = DeliveryTemplateCatalog.Definitions["application-security-external"];
        Assert.Equal("Security", category);
        Assert.Equal(["generic/application-security-external.yaml"], resources);

        var stages = YamlParsingHelper.FlattenJobs(Shipped("generic/application-security-external.yaml"));
        var steps = stages.SelectMany(stage => stage.Steps).ToList();

        Assert.All(steps, step => Assert.Contains(step.Type, new[] { "scanner", "analysis-gate" }));
        Assert.Equal(
            ["gitleaks", "opengrep", "trivy-dependencies", "trivy-iac"],
            steps.Where(step => step.Type == "scanner").Select(step => step.Scanner!).Order());
        Assert.Empty(PipelineSetupReadinessService.RequiredScannerFiles(stages));
        Assert.Empty(PipelineSetupReadinessService.RequiredScannerFiles(
            YamlParsingHelper.FlattenJobs(Shipped("generic/application-security.yaml"))));
    }

    /// <summary>The candidate template publishes the artifact produced by generic CI.</summary>
    [Fact]
    public void CandidateV2_ReleasesThePayloadTheGenericCiPackages_AndSkipsAbsentPipelines()
    {
        var candidate = Shipped("application-candidate-v2.yaml");
        var ciArtifact = Assert.Single(
            YamlParsingHelper.FlattenJobs(Shipped("generic/application-ci.yaml")).SelectMany(stage => stage.Steps),
            step => step.Type == "artifacts" && step.TargetFiles.Contains(".pipeline-artifacts/**")).ArtifactName;

        Assert.Equal(ciArtifact, candidate.Variables["APPLICATION_ARTIFACT_NAME"]);
        foreach (var (stage, toggle) in new[]
                 {
                     ("Quality", "APPLICATION_QUALITY_ENABLED"),
                     ("Security", "APPLICATION_SECURITY_ENABLED"),
                     ("QA", "APPLICATION_QA_ENABLED")
                 })
        {
            Assert.Equal($"eq(variables['{toggle}'], 'true')", candidate.Stages.Single(item => item.Name == stage).Condition);
            Assert.Equal("true", candidate.Variables[toggle]);
        }
    }

    [Fact]
    public async Task SeedAsync_ArchivesAndRemovesAnUnreferencedLegacyTemplate()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        var organization = new Organization
        {
            Name = "Aetheus",
            Slug = "aetheus",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.PipelineTemplates.Add(new PipelineTemplate
        {
            Name = "Build=VPS, Deploy=VPS",
            Description = "Old platform matrix",
            Category = "CI/CD",
            OrganizationId = organization.Id,
            LatestVersion = 1,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 1,
                    YamlContent = "name: old\nstages: []",
                    ChangelogEntry = "Initial version",
                    CreatedAt = DateTime.UnixEpoch,
                    CreatedByUsername = "system"
                }
            ]
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System)
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.False(await db.PipelineTemplates.AnyAsync(
            template => template.Name == "Build=VPS, Deploy=VPS",
            TestContext.Current.CancellationToken));
        var legacy = await db.PipelineTemplates.Include(template => template.Versions)
            .SingleAsync(template => template.Name == "legacy-build-vps-deploy-vps",
                TestContext.Current.CancellationToken);
        Assert.Equal("Legacy", legacy.Category);
        Assert.Equal("name: old\nstages: []", Assert.Single(legacy.Versions).YamlContent);
    }

    [Fact]
    public async Task SeedAsync_AppendsInsteadOfMutatingWhenAReferencePipelineEvolves()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        var organization = new Organization
        {
            Name = "Aetheus",
            Slug = "aetheus",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.PipelineTemplates.Add(new PipelineTemplate
        {
            Name = "application-ci",
            Description = "Old reference",
            Category = "Application Delivery",
            OrganizationId = organization.Id,
            LatestVersion = 1,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 1,
                    YamlContent = "name: application-ci\nstages: []",
                    ChangelogEntry = "Old published reference",
                    CreatedAt = DateTime.UnixEpoch,
                    CreatedByUsername = "system"
                }
            ]
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System)
            .SeedAsync(TestContext.Current.CancellationToken);

        var template = await db.PipelineTemplates.Include(item => item.Versions)
            .SingleAsync(item => item.Name == "application-ci", TestContext.Current.CancellationToken);
        Assert.Equal(2, template.LatestVersion);
        Assert.Equal("name: application-ci\nstages: []", template.Versions.Single(item => item.Version == 1).YamlContent);
        Assert.Contains("Run project CI commands", template.Versions.Single(item => item.Version == 2).YamlContent,
            StringComparison.Ordinal);
        Assert.Contains("name: Publish artifacts", template.Versions.Single(item => item.Version == 2).YamlContent,
            StringComparison.Ordinal);
        Assert.Contains("artifact_name: ApplicationPayload-artifacts",
            template.Versions.Single(item => item.Version == 2).YamlContent, StringComparison.Ordinal);
    }

    private static PipelineTemplate CreateHistoricalCandidate(int organizationId, string yamlContent, int versions) =>
        new()
        {
            Name = "application-candidate",
            Description = "Historical application candidate",
            Category = "Application Delivery",
            OrganizationId = organizationId,
            LatestVersion = versions,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
            Versions = Enumerable.Range(1, versions).Select(version => new PipelineTemplateVersion
            {
                Version = version,
                YamlContent = yamlContent,
                ChangelogEntry = "Historical published variant",
                CreatedAt = DateTime.UnixEpoch,
                CreatedByUsername = "system"
            }).ToList()
        };
}
