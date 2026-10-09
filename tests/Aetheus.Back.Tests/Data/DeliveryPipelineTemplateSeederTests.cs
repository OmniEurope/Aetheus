// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.DataSeed;

public sealed class DeliveryPipelineTemplateSeederTests
{
    /// <summary>
    /// Recette R2-094: the numbering restarted at v1 with the current contents. The candidate kept its
    /// v1 (same hash as before the reset), the promotion's v1 is its former v2 and the blue-green
    /// cutover's v1 its former v6. A published v1 never changes again: the seeder refuses it at startup.
    /// </summary>
    [Theory]
    [InlineData("application-candidate-v1.yaml", "e518dbb44946507c765c7206e1351b9cc03b2fd25e3c4be60a7783c9288d5788")]
    [InlineData("application-candidate-v2.yaml", "140793bce04abf756465316c11c73929af72e0aff3d4e04aa6b32153d8f9f421")]
    [InlineData("application-promotion-v1.yaml", "02ffed7982ade22b01b722f7fd312b348c66871d7fd03d8b2c9e58a9ea05b78c")]
    [InlineData("host-bluegreen-deploy-v1.yaml", "04722e3c4e8c9cc766c597b43ce71207b2ba92e1c3ccf04e1127050d0e884723")]
    public void ShippedVersions_HaveStableContent(string resource, string expectedSha256)
    {
        var normalized = DeliveryPipelineTemplateSeeder.ReadResource(resource)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        var actual = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();

        Assert.Equal(expectedSha256, actual);
    }

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
        Assert.Equal(DeliveryPipelineTemplateSeeder.Definitions.Count, templates.Count);
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
        // Recette R2-094: every shipped template restarted at v1; one shipped file per published version
        // since (the candidate's v2, recette R2-121).
        Assert.All(templates, template =>
        {
            var shippedFiles = DeliveryPipelineTemplateSeeder.Definitions[template.Name].Resources.Length;
            Assert.Equal(shippedFiles, template.LatestVersion);
            Assert.Equal(Enumerable.Range(1, shippedFiles), template.Versions.Select(version => version.Version).Order());
        });
        var candidate = Assert.Single(templates, template => template.Name == "application-candidate");
        Assert.Equal(2, candidate.LatestVersion);
        Assert.Contains("R2-121", candidate.Versions.Single(version => version.Version == 2).ChangelogEntry,
            StringComparison.Ordinal);
        Assert.Contains(templates, template => template.Name == "application-qa-extended");
        Assert.Contains(templates, template => template.Name == "application-security-history");
        var light = Assert.Single(templates, template => template.Name == "application-light");
        Assert.Equal(1, light.LatestVersion);
        Assert.Equal(1, Assert.Single(light.Versions).Version);
        // The blue-green cutover's v1 is the content last published as v6 (confirmation window).
        var blueGreen = Assert.Single(templates, template => template.Name == "host-bluegreen-deploy");
        Assert.Contains("confirm_minutes: \"$(BG_CONFIRM_MINUTES)\"", Assert.Single(blueGreen.Versions).YamlContent,
            StringComparison.Ordinal);
        // A fresh database has nothing to renumber; the reset is marked done all the same.
        Assert.True(await db.AppSettings.AnyAsync(setting => setting.Key == DeliveryTemplateVersionReset.MarkerKey,
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
        // Recette R2-094 kept the candidate's v1 content, the one aetheus-candidate always pinned.
        var versionOne = DeliveryPipelineTemplateSeeder.ReadResource("application-candidate-v1.yaml");

        Assert.Contains("release: previous-deployed", versionOne, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionTemplate_RequiresAnExplicitImmutableCandidateVersion()
    {
        // Recette R2-094: v1 is the content formerly published as v2.
        var versionOne = DeliveryPipelineTemplateSeeder.ReadResource("application-promotion-v1.yaml");

        Assert.Contains("required: true", versionOne, StringComparison.Ordinal);
        Assert.DoesNotContain("default: \"c-$(BUILD_SOURCEVERSION)\"", versionOne, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericPipelineTemplates_UseProjectAdaptersWithoutAetheusImplementationDetails()
    {
        var yaml = DeliveryPipelineTemplateSeeder.ReadResource("generic/application-nightly.yaml");
        var packages = DeliveryPipelineTemplateSeeder.ReadResource(
            "generic/publish-observability-packages.yaml");

        Assert.Contains("name: application-nightly-template", yaml, StringComparison.Ordinal);
        Assert.Contains("schedule: \"0 0 2 * * *\"", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_CI_PIPELINE", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_QUALITY_PIPELINE", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_SECURITY_PIPELINE", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CANDIDATE", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("type: release", yaml, StringComparison.Ordinal);
        Assert.Contains(".pipeline/scripts/publish-packages.sh", packages, StringComparison.Ordinal);
        Assert.DoesNotContain("Aetheus", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Aetheus", packages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenericPipelineTemplates_AreAdapterBasedAndContainNoAetheusProductPaths()
    {
        var genericResources = DeliveryPipelineTemplateSeeder.Definitions.Values
            .SelectMany(definition => definition.Resources)
            .Where(resource => resource.StartsWith("generic/", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(13, genericResources.Length);
        foreach (var resource in genericResources)
        {
            var yaml = DeliveryPipelineTemplateSeeder.ReadResource(resource);
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
                .Replace("AETHEUS_GIT_HISTORY_DEPTH", string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("Aetheus", withoutPlatformInterface, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sonytumen.com", yaml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/var/lib/", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("src/", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("tests/", yaml, StringComparison.Ordinal);
        }

        // The nightly only orchestrates; the security scans are the platform's own scanners.
        string[] adapterFree =
        [
            "generic/application-nightly.yaml",
            "generic/application-security.yaml",
            "generic/application-security-history.yaml"
        ];
        Assert.All(genericResources.Where(resource => !adapterFree.Contains(resource)),
            resource => Assert.Contains(".pipeline/scripts/",
                DeliveryPipelineTemplateSeeder.ReadResource(resource), StringComparison.Ordinal));
    }

    private static PipelineYamlDefinition Shipped(string resource) =>
        YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryPipelineTemplateSeeder.ReadResource(resource))!;

    private static IEnumerable<string> GenericResourcesAndCandidateV2() =>
        DeliveryPipelineTemplateSeeder.Definitions.Values
            .SelectMany(definition => definition.Resources)
            .Where(resource => resource.StartsWith("generic/", StringComparison.Ordinal))
            .Append("application-candidate-v2.yaml");

    /// <summary>
    /// Recette R2-121, ADR-012: the steps of a stage run concurrently. A restore beside the adapter
    /// that reads it, or a "mark deployed" beside the deployment, raced in the v1 generic templates.
    /// A stage that restores artifacts or writes a release does only that.
    /// </summary>
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

    /// <summary>Recette R2-121: a new project's quality and security runs carry a grade, on rules the
    /// deployed backend accepts (no finding_score until R2-119 ships), with every gate after its producers.</summary>
    [Theory]
    [InlineData("generic/application-quality.yaml", "quality")]
    [InlineData("generic/application-security.yaml", "security")]
    [InlineData("generic/application-security-history.yaml", "security")]
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

    /// <summary>Recette R2-121: the candidate a wizard creates releases the artifact the generic CI
    /// packages; v1 defaulted to Package-artifacts, which no generic pipeline produces.</summary>
    [Fact]
    public void CandidateV2_ReleasesThePayloadTheGenericCiPackages_AndSkipsAbsentPipelines()
    {
        var candidate = Shipped("application-candidate-v2.yaml");
        var ciArtifact = Assert.Single(Shipped("generic/application-ci.yaml").Stages).ArtifactName;

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
        Assert.Contains("Run project CI adapter", template.Versions.Single(item => item.Version == 2).YamlContent,
            StringComparison.Ordinal);
        Assert.Contains("name: Application payload", template.Versions.Single(item => item.Version == 2).YamlContent,
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
