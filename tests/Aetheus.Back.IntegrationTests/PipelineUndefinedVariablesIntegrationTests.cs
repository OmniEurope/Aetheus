// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-56 against the real services and a real PostgreSQL (the unit suite mocks the library and vault lookups): the
/// readiness verdict of a project names, for each undefined variable, the library or the vault of its pipeline that must
/// hold it, with the id of the one that really exists.
/// </summary>
public sealed class PipelineUndefinedVariablesIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Yaml = """
        name: w9-deploy-prod
        trigger: manual
        variable_libraries:
          - w9.share
        vaults:
          - w9.vault
        stages:
          - name: Deploy
            steps:
              - name: Run
                shell: echo $(API_HOST) $(DB_PASSWORD) $(JWT_KEY) $(HOST_PREFIX)
        """;

    [Fact]
    public async Task UndefinedVariables_NameTheRealLibraryOrVault_ByKindOfName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        int projectId, organizationId, libraryId, vaultId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            organizationId = await db.Organizations.Where(o => o.Slug == "aetheus").Select(o => o.Id).FirstAsync(ct);
            var project = new Project { Name = "W9", OrganizationId = organizationId };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            projectId = project.Id;
            db.Pipelines.Add(new Pipeline
            {
                Name = "w9-deploy-prod",
                ProjectId = projectId,
                YamlDefinition = Yaml,
                TriggerType = PipelineTriggerType.Manual
            });
            await db.SaveChangesAsync(ct);
            var libraries = scope.ServiceProvider.GetRequiredService<IVariableLibraryService>();
            var library = await libraries.CreateLibraryAsync(new CreateVariableLibraryRequest { Name = "w9.share", ProjectId = projectId }, ct);
            libraryId = library.Id;
            // HOST_PREFIX is defined, so it is not reported; the other three names are in no library or vault.
            await libraries.CreateEntryAsync(library.Id, new CreateVariableEntryRequest { Key = "HOST_PREFIX", Value = "w9" }, ct);
            var vaults = scope.ServiceProvider.GetRequiredService<IVaultService>();
            var vault = await vaults.CreateVaultAsync(new CreateVaultRequest { Name = "w9.vault", ProjectId = projectId }, ct);
            vaultId = vault.Id;
        }

        using var check = factory.Services.CreateScope();
        var readiness = await check.ServiceProvider.GetRequiredService<IPipelineSetupReadinessService>()
            .CheckProjectAsync(projectId, pipelineId: null, organizationId, ct);

        var undefined = Assert.Single(readiness.Checks, found => found.Kind == PipelineSetupReadinessKind.UndefinedVariables);
        Assert.Equal(["API_HOST", "DB_PASSWORD", "JWT_KEY"], undefined.Items);
        var rows = undefined.UndefinedVariables.ToDictionary(row => row.Name);
        Assert.Equal((PipelineSetupReadinessContainerKind.Library, libraryId, "w9.share"),
            (rows["API_HOST"].Container, rows["API_HOST"].OwnerId, rows["API_HOST"].OwnerName));
        foreach (var secret in new[] { "DB_PASSWORD", "JWT_KEY" })
            Assert.Equal((PipelineSetupReadinessContainerKind.Vault, vaultId, "w9.vault"),
                (rows[secret].Container, rows[secret].OwnerId, rows[secret].OwnerName));
    }
}
