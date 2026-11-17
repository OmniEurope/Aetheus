// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.IntegrationTests;

public sealed class PipelineFleetPagingIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task FleetQuery_FiltersSortsAndPaginatesBeforeMaterializationOnPostgres()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var organization = new Organization
        {
            Name = $"org-{Guid.NewGuid():N}",
            Slug = $"s{Guid.NewGuid():N}"[..12]
        };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var project = new Project
        {
            Name = "Fleet project",
            OrganizationId = organization.Id
        };
        db.Projects.Add(project);
        db.PipelineTemplates.Add(new PipelineTemplate
        {
            Name = "ci",
            Category = "CI",
            OrganizationId = organization.Id,
            LatestVersion = 3
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Pipelines.AddRange(Enumerable.Range(1, 30).Select(index => new Pipeline
        {
            Name = $"pipeline-{index:D2}",
            ProjectId = project.Id,
            YamlDefinition = $"name: pipeline-{index:D2}\nextends: ci@3\nstages: []",
            TemplateReferenceName = "ci",
            TemplateReferenceVersion = 3
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new PipelineFleetRepository(db).GetPageAsync(
            new PipelineFleetPaginationRequest
            {
                Page = 2,
                PageSize = 5,
                Freshness = PipelineFleetFreshness.Current,
                SortBy = "PipelineName",
                SortDescending = true
            },
            [organization.Id],
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(30, result.TotalCount);
        Assert.Equal(
            ["pipeline-25", "pipeline-24", "pipeline-23", "pipeline-22", "pipeline-21"],
            result.Items.Select(item => item.PipelineName));
        Assert.All(result.Items, item => Assert.Equal(PipelineFleetFreshness.Current, item.Freshness));
    }
}
