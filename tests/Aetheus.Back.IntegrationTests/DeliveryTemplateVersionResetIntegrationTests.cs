// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Seeding;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>Concurrent startup serializes shipped-template renumbering through the advisory lock,
/// removing previous rows before inserting version one under the uniqueness constraint.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DeliveryTemplateVersionResetIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task TwoColoursStartingTogether_RenumberOnce()
    {
        await ResetAndMigrateAsync();
        int pipelineId;
        await using (var setup = NewContext())
        {
            // The migrations create the default organization.
            var organization = await setup.Organizations.SingleAsync(
                item => item.Slug == "aetheus", TestContext.Current.CancellationToken);
            Assert.False(await setup.PipelineTemplates.AnyAsync(TestContext.Current.CancellationToken));
            var project = new Project { Name = "aetheus", Organization = organization };
            setup.Projects.Add(project);
            // Every shipped template exists already, as in production: only the renumbering differs.
            foreach (var (name, definition) in DeliveryTemplateCatalog.Definitions)
            {
                var shipped = DeliveryTemplateCatalog.ReadResource(definition.Resources[0]);
                var latest = name == "host-bluegreen-deploy" ? 6 : 1;
                setup.PipelineTemplates.Add(new PipelineTemplate
                {
                    Name = name,
                    Description = definition.Description,
                    Category = definition.Category,
                    Organization = organization,
                    LatestVersion = latest,
                    CreatedAt = DateTime.UnixEpoch,
                    UpdatedAt = DateTime.UnixEpoch,
                    Versions = Enumerable.Range(1, latest).Select(version => new PipelineTemplateVersion
                    {
                        Version = version,
                        YamlContent = version == latest ? shipped : $"name: {name}-old-v{version}\nstages: []",
                        ChangelogEntry = $"v{version}",
                        CreatedAt = DateTime.UnixEpoch,
                        CreatedByUsername = "system"
                    }).ToList()
                });
            }
            var pipeline = new Pipeline
            {
                Name = "aetheus-deploy-prod",
                Project = project,
                YamlDefinition = "name: aetheus-deploy-prod\nextends: host-bluegreen-deploy@6\nstages: []\n",
                TemplateReferenceName = "host-bluegreen-deploy",
                TemplateReferenceVersion = 6
            };
            setup.Pipelines.Add(pipeline);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            pipelineId = pipeline.Id;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();
        await using var blue = NewContext();
        await using var green = NewContext();
        await Task.WhenAll(
            Seeder(blue, configuration).SeedAsync(TestContext.Current.CancellationToken),
            Seeder(green, configuration).SeedAsync(TestContext.Current.CancellationToken));

        await using var verify = NewContext();
        var template = await verify.PipelineTemplates.Include(item => item.Versions)
            .SingleAsync(item => item.Name == "host-bluegreen-deploy", TestContext.Current.CancellationToken);
        // The reset renumbers to the first shipped file as v1; the same startup then publishes the later
        // shipped file (v2) on top of it, so both colours end with exactly v1 and v2.
        var shippedFiles = DeliveryTemplateCatalog.Definitions["host-bluegreen-deploy"].Resources;
        Assert.Equal(shippedFiles.Length, template.LatestVersion);
        Assert.Equal(Enumerable.Range(1, shippedFiles.Length), template.Versions.Select(item => item.Version).Order());
        var version = template.Versions.Single(item => item.Version == 1);
        Assert.Equal(DeliveryTemplateCatalog.ReadResource(shippedFiles[0]), version.YamlContent);
        Assert.Equal(1, await verify.AppSettings.CountAsync(
            setting => setting.Key == DeliveryTemplateVersionReset.MarkerKey, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.AppSettings.CountAsync(
            setting => setting.Key == DeliveryTemplateVersionReset.FirstResetMarkerKey, TestContext.Current.CancellationToken));
        var pipelineAfter = await verify.Pipelines.SingleAsync(item => item.Id == pipelineId, TestContext.Current.CancellationToken);
        Assert.Equal(1, pipelineAfter.TemplateReferenceVersion);
        Assert.Contains("extends: host-bluegreen-deploy@1", pipelineAfter.YamlDefinition, StringComparison.Ordinal);
    }

    private static DeliveryPipelineTemplateSeeder Seeder(AppDbContext db, IConfiguration configuration) =>
        new(db, TimeProvider.System,
            lease: new PostgresLeaderLease(configuration, NullLogger<PostgresLeaderLease>.Instance));
}
