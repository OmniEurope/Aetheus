// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Audit BACK-018 on a real PostgreSQL: the environment a run's gates read is its project's own, then a
/// shared one, never a homonym of another project, whatever order the rows were written in; and the run's
/// project is found through the pipeline whichever way the pipeline is owned.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineEnvironmentProjectScopeIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task TheRunsProjectProd_IsTheOneFound_NotTheOlderHomonym()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        int firstProject, secondProject, secondProd, sharedQa, projectRun, environmentRun;
        await using (var seed = NewContext())
        {
            var organization = new Organization { Name = "Scope", Slug = $"scope-{Guid.NewGuid():N}"[..20] };
            seed.Organizations.Add(organization);
            await seed.SaveChangesAsync(ct);
            var first = new Project { Name = "First", OrganizationId = organization.Id };
            var second = new Project { Name = "Second", OrganizationId = organization.Id };
            seed.Projects.AddRange(first, second);
            await seed.SaveChangesAsync(ct);
            seed.Environments.Add(new Environment { Name = "prod", ProjectId = first.Id });
            await seed.SaveChangesAsync(ct);
            var prod = new Environment { Name = "prod", ProjectId = second.Id, RequireApproval = true };
            var qa = new Environment { Name = "qa" };
            var staging = new Environment { Name = "staging", ProjectId = second.Id };
            seed.Environments.AddRange(prod, qa, staging);
            await seed.SaveChangesAsync(ct);
            var owned = new Pipeline { Name = "deliver", ProjectId = second.Id, YamlDefinition = "name: deliver\nstages: []" };
            var byEnvironment = new Pipeline { Name = "smoke", EnvironmentId = staging.Id, YamlDefinition = "name: smoke\nstages: []" };
            seed.Pipelines.AddRange(owned, byEnvironment);
            await seed.SaveChangesAsync(ct);
            var run = new PipelineRun { PipelineId = owned.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
            var smoke = new PipelineRun { PipelineId = byEnvironment.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
            seed.PipelineRuns.AddRange(run, smoke);
            await seed.SaveChangesAsync(ct);
            (firstProject, secondProject, secondProd, sharedQa, projectRun, environmentRun) =
                (first.Id, second.Id, prod.Id, qa.Id, run.Id, smoke.Id);
        }

        await using var db = NewContext();
        var core = new PipelineRepository(
            db, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRepository>.Instance,
            new Aetheus.Back.Components.Tasks.PipelineTaskLifecycleRepository(db, TimeProvider.System),
            new PipelineRunLineageRepository(db));

        Assert.Equal(secondProject, await core.GetRunProjectIdAsync(projectRun, ct));
        Assert.Equal(secondProject, await core.GetRunProjectIdAsync(environmentRun, ct));
        var found = await core.FindEnvironmentByNameAsync("prod", secondProject, ct);
        Assert.Equal(secondProd, found?.Id);
        Assert.True(found!.RequireApproval);
        Assert.Equal(sharedQa, (await core.FindEnvironmentByNameAsync("qa", secondProject, ct))?.Id);
        Assert.Null(await core.FindEnvironmentByNameAsync("staging", firstProject, ct));
        Assert.Null(await core.FindEnvironmentByNameAsync("prod", null, ct));
    }
}
