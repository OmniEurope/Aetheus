// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
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
            foreach (var (name, definition) in DeliveryPipelineTemplateSeeder.Definitions)
            {
                var shipped = DeliveryPipelineTemplateSeeder.ReadResource(definition.Resources[0]);
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
        Assert.Equal(1, template.LatestVersion);
        var version = Assert.Single(template.Versions);
        Assert.Equal(1, version.Version);
        Assert.Equal(DeliveryPipelineTemplateSeeder.ReadResource("host-bluegreen-deploy-v1.yaml"), version.YamlContent);
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
