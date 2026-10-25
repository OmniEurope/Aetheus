// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The setup wizard's "start from an existing project" against the real services and a real PostgreSQL
/// (the unit suite mocks every collaborator): a copied scheduled pipeline is created with its trigger
/// off, the identifiers the structure marks as the source's own are renamed, the repository files it
/// depends on are named, and the library and vault it copies hold EMPTY values that the readiness
/// verdict lists and that a run refuses, until they are filled in.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineProjectCopyIntegrationTests(PostgresFixture fixture)
{
    private const string NightlyYaml = """
        name: aetheus-nightly
        trigger: schedule
        schedule: "0 0 3 * * *"
        variable_libraries:
          - aetheus-prod-host
        vaults:
          - aetheus-deploy
        variables:
          COMPOSE_PROJECT_NAME: aetheus-nightly-run
          SITE: aetheus.example.com
        stages:
          - name: Run
            environment: aetheus-staging
            steps:
              - name: go
                shell: sh .pipeline/scripts/nightly.sh $(API_HOST)
        """;

    private const string HookYaml = """
        name: aetheus-hook
        trigger: webhook
        branches:
          - main
        stages:
          - name: Build
            steps:
              - name: build
                shell: echo build
        """;

    private sealed record Seeded(int SourceProjectId, int TargetProjectId, int OrganizationId);

    private static async Task<Seeded> SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.Where(o => o.Slug == "aetheus").Select(o => o.Id).FirstAsync(ct);
        var source = new Project { Name = "Aetheus Source", OrganizationId = organizationId };
        var target = new Project { Name = "Atlas Target", OrganizationId = organizationId };
        db.Projects.AddRange(source, target);
        await db.SaveChangesAsync(ct);
        db.Pipelines.AddRange(
            new Pipeline
            {
                Name = "aetheus-nightly",
                ProjectId = source.Id,
                YamlDefinition = NightlyYaml,
                TriggerType = PipelineTriggerType.Schedule
            },
            new Pipeline
            {
                Name = "aetheus-hook",
                ProjectId = source.Id,
                YamlDefinition = HookYaml,
                TriggerType = PipelineTriggerType.Webhook
            });
        await db.SaveChangesAsync(ct);

        var libraries = scope.ServiceProvider.GetRequiredService<IVariableLibraryService>();
        var library = await libraries.CreateLibraryAsync(
            new CreateVariableLibraryRequest { Name = "aetheus-prod-host", ProjectId = source.Id }, ct);
        await libraries.CreateEntryAsync(library.Id, new CreateVariableEntryRequest { Key = "API_HOST", Value = "vps.aetheus.example" }, ct);
        await libraries.CreateEntryAsync(library.Id, new CreateVariableEntryRequest { Key = "PORT", Value = "10025" }, ct);
        var vaults = scope.ServiceProvider.GetRequiredService<IVaultService>();
        var vault = await vaults.CreateVaultAsync(new CreateVaultRequest { Name = "aetheus-deploy", ProjectId = source.Id }, ct);
        await vaults.CreateSecretAsync(vault.Id, new CreateVaultSecretRequest { Key = "DB_PASSWORD", Value = "source-secret-value" }, ct);
        return new Seeded(source.Id, target.Id, organizationId);
    }

    private static PipelineProjectCopyRequest Request(Seeded seeded) => new()
    {
        SourceProjectId = seeded.SourceProjectId,
        TargetProjectId = seeded.TargetProjectId,
        SourcePrefix = "aetheus",
        Prefix = "ats"
    };

    [Fact]
    public async Task Copy_OnARealDatabase_DisablesTriggers_RenamesIdentifiers_NamesRepositoryFiles_AndFlagsEmptyValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var seeded = await SeedAsync(factory.Services, ct);

        PipelineProjectCopyResultDto result;
        using (var scope = factory.Services.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<IPipelineProjectCopyService>()
                .CopyAsync(Request(seeded), seeded.OrganizationId, null, null, null, ct);
        }

        // Defect 2: both copies are created, with their trigger off, in the stored YAML and in the
        // column the scheduler and the webhook handler select on.
        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var copies = await db.Pipelines.AsNoTracking()
            .Where(p => p.ProjectId == seeded.TargetProjectId).ToDictionaryAsync(p => p.Name, ct);
        Assert.Equal(["ats-hook", "ats-nightly"], copies.Keys.Order());
        Assert.All(copies.Values, copy => Assert.Equal(PipelineTriggerType.Manual, copy.TriggerType));
        Assert.Contains("trigger: manual", copies["ats-nightly"].YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("schedule: \"0 0 3 * * *\"", copies["ats-nightly"].YamlDefinition, StringComparison.Ordinal);
        var autoTriggers = check.ServiceProvider.GetRequiredService<IPipelineAutoTriggerRepository>();
        Assert.DoesNotContain(await autoTriggers.GetScheduledPipelinesAsync(ct), p => p.ProjectId == seeded.TargetProjectId);
        Assert.DoesNotContain(
            await autoTriggers.GetWebhookTriggeredPipelinesForProjectAsync(seeded.TargetProjectId, ct), p => p.ProjectId == seeded.TargetProjectId);
        // The source pipelines are untouched and still triggered.
        Assert.Contains(await autoTriggers.GetScheduledPipelinesAsync(ct), p => p.Name == "aetheus-nightly");
        var disabled = result.Plan.Pipelines.SelectMany(item => item.Issues)
            .Where(issue => issue.Kind == PipelineCopyIssueKind.TriggerDisabled).Select(issue => issue.Detail).Order();
        Assert.Equal(["schedule", "webhook"], disabled);

        // Defect 3: the identifiers the copy can recognise are renamed; the address is left and listed.
        var nightly = copies["ats-nightly"].YamlDefinition;
        Assert.Contains("COMPOSE_PROJECT_NAME: ats-nightly-run", nightly, StringComparison.Ordinal);
        Assert.Contains("environment: ats-staging", nightly, StringComparison.Ordinal);
        Assert.Contains("- ats-prod-host", nightly, StringComparison.Ordinal);
        Assert.Contains("SITE: aetheus.example.com", nightly, StringComparison.Ordinal);
        var nightlyItem = result.Plan.Pipelines.Single(item => item.NewName == "ats-nightly");
        Assert.Contains("aetheus.example.com",
            nightlyItem.Issues.Single(issue => issue.Kind == PipelineCopyIssueKind.SourceProjectNameRemains).Detail,
            StringComparison.Ordinal);
        Assert.Contains(nightlyItem.Issues, issue => issue.Kind == PipelineCopyIssueKind.IdentifiersRenamed);

        // Defect 4: the repository file is named, not solved.
        Assert.Equal(".pipeline/scripts/nightly.sh",
            nightlyItem.Issues.Single(issue => issue.Kind == PipelineCopyIssueKind.RepositoryFileDependency).Detail);

        // Defect 1: the copied library and vault hold empty values (checked in the database) ...
        var libraries = check.ServiceProvider.GetRequiredService<IVariableLibraryService>();
        var vaults = check.ServiceProvider.GetRequiredService<IVaultService>();
        var (libraryValues, _) = await libraries.ResolveLibrariesWithCrossAccessAndNamesAsync(
            ["ats-prod-host"], seeded.TargetProjectId, ct);
        Assert.Equal(["API_HOST", "PORT"], libraryValues.Keys.Order());
        Assert.All(libraryValues.Values, value => Assert.Equal(string.Empty, value));
        var (secretValues, _) = await vaults.ResolveVaultSecretsWithCrossAccessAndNamesAsync(
            ["ats-deploy"], seeded.TargetProjectId, ct);
        Assert.Equal(string.Empty, Assert.Single(secretValues).Value);
        Assert.DoesNotContain("source-secret-value", secretValues.Values);

        // ... and the copy's own readiness verdict lists them.
        var emptyLibrary = Assert.Single(result.Readiness.Checks, found => found.Kind == PipelineSetupReadinessKind.EmptyLibraryValues);
        Assert.Equal(["API_HOST"], emptyLibrary.Items);
        var emptyVault = Assert.Single(result.Readiness.Checks, found => found.Kind == PipelineSetupReadinessKind.EmptyVaultSecrets);
        Assert.Equal(["DB_PASSWORD"], emptyVault.Items);

        // A run that consumes them stops with a clear message, then goes through once they are filled in.
        var resolver = check.ServiceProvider.GetRequiredService<IPipelineVariableResolver>();
        var definition = YamlParsingHelper.ParseAndValidate(nightly)!;
        var refused = await Assert.ThrowsAsync<BadRequestException>(() => resolver.ResolveVariablesWithWarningsAsync(
            definition, seeded.TargetProjectId, null, ct, enforceResolvedReferences: true));
        Assert.Contains("'API_HOST'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'DB_PASSWORD'", refused.Message, StringComparison.Ordinal);

        await FillInAsync(libraries, vaults, seeded.TargetProjectId, ct);
        // A resolver memoizes per request: the next run is a new scope.
        using var nextRun = factory.Services.CreateScope();
        var (resolved, _, _) = await nextRun.ServiceProvider.GetRequiredService<IPipelineVariableResolver>()
            .ResolveVariablesWithWarningsAsync(
                definition, seeded.TargetProjectId, null, ct, enforceResolvedReferences: true);
        Assert.Equal("api.atlas.example", resolved["API_HOST"]);
        var after = await nextRun.ServiceProvider.GetRequiredService<IPipelineSetupReadinessService>()
            .CheckProjectAsync(seeded.TargetProjectId, pipelineId: null, seeded.OrganizationId, ct);
        Assert.DoesNotContain(after.Checks, found =>
            found.Kind is PipelineSetupReadinessKind.EmptyLibraryValues or PipelineSetupReadinessKind.EmptyVaultSecrets);
    }

    private static async Task FillInAsync(
        IVariableLibraryService libraries, IVaultService vaults, int projectId, CancellationToken ct)
    {
        var library = (await libraries.GetLibrariesAsync(projectId, ct: ct)).Items.Single(item => item.Name == "ats-prod-host");
        var entries = await libraries.ExportEntriesAsync(library.Id, ct);
        foreach (var entry in entries)
            await libraries.UpdateEntryAsync(library.Id, entry.Id,
                new UpdateVariableEntryRequest { Key = entry.Key, Value = entry.Key == "API_HOST" ? "api.atlas.example" : "10026" }, ct);
        var vault = (await vaults.GetVaultsAsync(projectId, ct: ct)).Items.Single(item => item.Name == "ats-deploy");
        var detail = await vaults.GetVaultDetailAsync(vault.Id, ct);
        var secret = Assert.Single(detail!.Secrets);
        await vaults.UpdateSecretAsync(vault.Id, secret.Id,
            new UpdateVaultSecretRequest { Key = secret.Key, Value = "target-secret-value" }, ct);
    }
}
