// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.DataSeed;

/// <summary>
/// Recette R2-094 (decided 2026-10-04, option a): every shipped delivery template restarts at v1 with
/// its current content, the pipelines extending it are pinned to @1, once per database.
/// </summary>
public sealed class DeliveryTemplateVersionResetTests
{
    private const string DeployProdYaml =
        "name: aetheus-deploy-prod\nextends: host-bluegreen-deploy@6 # pinned\nstages: []\n";

    [Fact]
    public async Task SeedAsync_RenumbersAnInflatedCatalogToOneV1AndRepinsItsPipelines()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        AddTemplate(db, organizationId, "host-bluegreen-deploy", 6, Shipped("host-bluegreen-deploy"));
        AddTemplate(db, organizationId, "application-candidate", 7, null, firstVersionYaml: Shipped("application-candidate"));
        AddTemplate(db, organizationId, "application-promotion", 2, Shipped("application-promotion"));
        AddTemplate(db, organizationId, "application-ci", 3, null);
        var deployProd = AddPipeline(db, projectId, "aetheus-deploy-prod", DeployProdYaml, "host-bluegreen-deploy", 6);
        var candidateYaml = "name: aetheus-candidate\nextends: application-candidate@1\nstages: []\n";
        var candidate = AddPipeline(db, projectId, "aetheus-candidate", candidateYaml, "application-candidate", 1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var audit = Substitute.For<IAuditService>();

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit)
            .SeedAsync(TestContext.Current.CancellationToken);

        foreach (var name in new[] { "host-bluegreen-deploy", "application-candidate", "application-promotion", "application-ci" })
        {
            var template = await LoadTemplateAsync(db, organizationId, name);
            // Renumbered to one v1 holding the v1 file; a template that ships a later file (the
            // candidate's v2, R2-121) then gets it from the seeding, after the reset.
            var shippedFiles = DeliveryPipelineTemplateSeeder.Definitions[name].Resources;
            Assert.Equal(shippedFiles.Length, template.LatestVersion);
            Assert.Equal(Enumerable.Range(1, shippedFiles.Length), template.Versions.Select(item => item.Version).Order());
            var version = template.Versions.Single(item => item.Version == 1);
            Assert.Equal(Shipped(name), version.YamlContent);
            Assert.Equal(DeliveryTemplateVersionReset.ChangelogEntry, version.ChangelogEntry);
        }
        var repinned = await db.Pipelines.SingleAsync(pipeline => pipeline.Id == deployProd.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, repinned.TemplateReferenceVersion);
        Assert.Equal(DeployProdYaml.Replace("@6", "@1", StringComparison.Ordinal), repinned.YamlDefinition);
        var unchanged = await db.Pipelines.SingleAsync(pipeline => pipeline.Id == candidate.Id, TestContext.Current.CancellationToken);
        Assert.Equal(candidateYaml, unchanged.YamlDefinition);
        await audit.Received(1).LogAsync("TemplateVersionsReset", "PipelineTemplate", Arg.Any<int?>(),
            "host-bluegreen-deploy: v6 -> v1", Arg.Any<CancellationToken>());
        await audit.Received(4).LogAsync("TemplateVersionsReset", "PipelineTemplate", Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await audit.Received(1).LogAsync("TemplateVersionRepinned", "Pipeline", deployProd.Id,
            "host-bluegreen-deploy: v6 -> v1", Arg.Any<CancellationToken>());
        await audit.Received(1).LogAsync("TemplateVersionRepinned", "Pipeline", Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.True(await db.AppSettings.AnyAsync(setting => setting.Key == DeliveryTemplateVersionReset.MarkerKey,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Once done, the reset never runs again: a generic template that later grows a v2 through the
    /// normal synchronization keeps it, and nothing is rewritten or audited on the next start.
    /// </summary>
    [Fact]
    public async Task SeedAsync_SecondStartIsANoOpAndLaterVersionsAreKept()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        AddTemplate(db, organizationId, "host-bluegreen-deploy", 6, Shipped("host-bluegreen-deploy"));
        AddPipeline(db, projectId, "aetheus-deploy-prod", DeployProdYaml, "host-bluegreen-deploy", 6);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System).SeedAsync(TestContext.Current.CancellationToken);
        var versionIds = await db.PipelineTemplateVersions.OrderBy(version => version.Id)
            .Select(version => version.Id).ToListAsync(TestContext.Current.CancellationToken);
        var ci = await LoadTemplateAsync(db, organizationId, "application-ci");
        ci.Versions.Add(new PipelineTemplateVersion
        {
            Version = 2,
            YamlContent = Shipped("application-ci"),
            ChangelogEntry = "Synchronized from the current reference pipeline.",
            CreatedAt = DateTime.UnixEpoch,
            CreatedByUsername = "system"
        });
        ci.LatestVersion = 2;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var audit = Substitute.For<IAuditService>();

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit)
            .SeedAsync(TestContext.Current.CancellationToken);

        var after = await db.PipelineTemplateVersions.OrderBy(version => version.Id)
            .Select(version => version.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(versionIds.Count + 1, after.Count);
        Assert.Equal(versionIds, after.Take(versionIds.Count));
        var reloaded = await LoadTemplateAsync(db, organizationId, "application-ci");
        Assert.Equal(2, reloaded.LatestVersion);
        Assert.Equal([1, 2], reloaded.Versions.Select(version => version.Version).Order());
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task SeedAsync_LeavesUserTemplatesAndOtherOrganizationsUntouched()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        var (otherOrganizationId, otherProjectId) = await SeedOrganizationAsync(db, "acme");
        AddTemplate(db, organizationId, "my-ci", 3, null);
        var userPipeline = AddPipeline(db, projectId, "uses-my-ci",
            "name: uses-my-ci\nextends: my-ci@3\nstages: []\n", "my-ci", 3);
        AddTemplate(db, otherOrganizationId, "host-bluegreen-deploy", 3, null);
        var otherPipeline = AddPipeline(db, otherProjectId, "acme-deploy",
            "name: acme-deploy\nextends: host-bluegreen-deploy@3\nstages: []\n", "host-bluegreen-deploy", 3);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System).SeedAsync(TestContext.Current.CancellationToken);

        var userTemplate = await LoadTemplateAsync(db, organizationId, "my-ci");
        Assert.Equal(3, userTemplate.LatestVersion);
        Assert.Equal([1, 2, 3], userTemplate.Versions.Select(version => version.Version).Order());
        var otherTemplate = await LoadTemplateAsync(db, otherOrganizationId, "host-bluegreen-deploy");
        Assert.Equal(3, otherTemplate.LatestVersion);
        Assert.Equal([1, 2, 3], otherTemplate.Versions.Select(version => version.Version).Order());
        foreach (var pipeline in new[] { userPipeline, otherPipeline })
        {
            var reloaded = await db.Pipelines.SingleAsync(item => item.Id == pipeline.Id, TestContext.Current.CancellationToken);
            Assert.Equal(3, reloaded.TemplateReferenceVersion);
            Assert.Contains("@3", reloaded.YamlDefinition, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("name: a\nextends: 'host-bluegreen-deploy@6' # keep\nstages: []", "name: a\nextends: 'host-bluegreen-deploy@1' # keep\nstages: []")]
    [InlineData("extends: \"application-promotion@12\"\r\nstages: []", "extends: \"application-promotion@1\"\r\nstages: []")]
    [InlineData("  extends :  host-bluegreen-deploy@2", "  extends :  host-bluegreen-deploy@1")]
    [InlineData("name: plain\nstages: []", "name: plain\nstages: []")]
    public void RepinExtends_RewritesOnlyTheVersion(string yaml, string expected) =>
        Assert.Equal(expected, DeliveryTemplateVersionReset.RepinExtends(yaml));

    private static string Shipped(string name) =>
        DeliveryPipelineTemplateSeeder.ReadResource(DeliveryPipelineTemplateSeeder.Definitions[name].Resources[0]);

    private static AppDbContext NewDatabase() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static async Task<(int OrganizationId, int ProjectId)> SeedOrganizationAsync(AppDbContext db, string slug)
    {
        var organization = new Organization
        {
            Name = slug,
            Slug = slug,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
        var project = new Project { Name = $"{slug}-project", Organization = organization };
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (organization.Id, project.Id);
    }

    /// <summary>Versions 1..<paramref name="latest"/>; the last holds <paramref name="latestYaml"/> when given.</summary>
    private static void AddTemplate(
        AppDbContext db, int organizationId, string name, int latest, string? latestYaml, string? firstVersionYaml = null) =>
        db.PipelineTemplates.Add(new PipelineTemplate
        {
            Name = name,
            Description = "Before the reset",
            Category = "Application Delivery",
            OrganizationId = organizationId,
            LatestVersion = latest,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
            Versions = Enumerable.Range(1, latest).Select(version => new PipelineTemplateVersion
            {
                Version = version,
                YamlContent = version == latest && latestYaml is not null ? latestYaml
                    : version == 1 && firstVersionYaml is not null ? firstVersionYaml
                    : $"name: {name}-old-v{version}\nstages: []",
                ChangelogEntry = $"v{version}",
                CreatedAt = DateTime.UnixEpoch,
                CreatedByUsername = "system"
            }).ToList()
        });

    private static Pipeline AddPipeline(
        AppDbContext db, int projectId, string name, string yaml, string templateName, int templateVersion)
    {
        var pipeline = new Pipeline
        {
            Name = name,
            ProjectId = projectId,
            YamlDefinition = yaml,
            TemplateReferenceName = templateName,
            TemplateReferenceVersion = templateVersion
        };
        db.Pipelines.Add(pipeline);
        return pipeline;
    }

    private static Task<PipelineTemplate> LoadTemplateAsync(AppDbContext db, int organizationId, string name) =>
        db.PipelineTemplates.Include(template => template.Versions)
            .SingleAsync(template => template.OrganizationId == organizationId && template.Name == name,
                TestContext.Current.CancellationToken);
}
