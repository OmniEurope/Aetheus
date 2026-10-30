// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-64: the libraries the pipeline setup wizard provisions for a project (<c>w6.share</c>,
/// <c>w6.qa</c>, <c>w6.prod</c>) list the pipelines the wizard created for it. The whole chain is real: the
/// shipped templates seeded the way the backend seeds them, the wizard's YAML (<c>extends: template@N</c>, the
/// production block), the template resolver, the usage repository and service, on PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WizardCreatedLibraryUsageIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The pipelines the wizard creates, by suffix, with the template each one extends (PipelineSetupRoles).
    private static readonly (string Suffix, string Template)[] Roles =
    [
        ("ci", "application-ci"),
        ("quality", "application-quality"),
        ("security", "application-security"),
        ("qa", "application-qa"),
        ("deploy-prod", "application-deploy-prod"),
        ("release-fast", "application-release-fast")
    ];

    /// <summary>What <c>PipelineSetupYamlBuilder.Build</c> writes: the pinned extends, the variables, and for the
    /// two deployment templates the server parameter and the production block.</summary>
    private static string WizardYaml(string prefix, string suffix, string template, int version)
    {
        var deploys = template is "application-deploy-prod" or "application-release-fast";
        return $"name: {prefix}-{suffix}\nextends: {template}@{version}\ntrigger: manual\n"
            + (deploys
                ? "parameters:\n  - name: deployServer\n    display_name: Production server\n    display_name_fr: Serveur de production\n"
                  + "    description: Name of the project's server the deployment runs on.\n"
                  + "    description_fr: Nom du serveur du projet sur lequel le déploiement s'exécute.\n"
                  + "    type: choice\n    required: true\n    allowed_values:\n      - \"srv-1\"\n    default: \"srv-1\"\n"
                : string.Empty)
            + $"variables:\n  APPLICATION_PREFIX: \"{prefix}\"\n  APPLICATION_HAS_FRONTEND: \"true\"\n"
            + $"  APPLICATION_DEPLOY_PIPELINE: \"{prefix}-deploy-prod\"\n"
            + (template == "application-deploy-prod"
                ? "  COMPOSE_FILE: \"docker-compose.yml\"\n  DEPLOY_STRATEGY: \"bluegreen\"\n  DEPLOY_HAS_API: \"false\"\n"
                : string.Empty)
            + "stages: []";
    }

    [Fact]
    public async Task TheLibrariesTheWizardProvisions_ListThePipelinesTheWizardCreated()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        // A customer organization of its own: its pipelines reach the shipped templates through the platform
        // organization, as every project but Aetheus's own does.
        var customer = new Organization { Name = "Customer", Slug = "customer", CreatedAt = DateTime.UnixEpoch, UpdatedAt = DateTime.UnixEpoch };
        var project = new Project { Name = "W6", Organization = customer };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System).SeedAsync(Ct);

        var latest = await db.PipelineTemplates.ToDictionaryAsync(template => template.Name, template => template.LatestVersion, Ct);
        foreach (var (suffix, template) in Roles)
            db.Pipelines.Add(new Pipeline
            {
                Name = $"W6-{suffix}",
                ProjectId = project.Id,
                YamlDefinition = WizardYaml("W6", suffix, template, latest[template]),
                TemplateReferenceName = template,
                TemplateReferenceVersion = latest[template]
            });
        // The names the provisioning gives the libraries: the project's slug, then share, qa and prod.
        var share = new VariableLibrary { Name = "w6.share", ProjectId = project.Id };
        var qa = new VariableLibrary { Name = "w6.qa", ProjectId = project.Id };
        var prod = new VariableLibrary { Name = "w6.prod", ProjectId = project.Id };
        db.VariableLibraries.AddRange(share, qa, prod);
        await db.SaveChangesAsync(Ct);

        var sut = new PipelineResourceUsageService(
            new PipelineResourceUsageRepository(db), new PipelineTemplateRepository(db), NullLogger<PipelineResourceUsageService>.Instance);
        var usage = await sut.GetAsync(null, null, null, Ct);

        Assert.Equal(["W6-deploy-prod", "W6-qa", "W6-release-fast"], usage.Libraries[share.Id].Select(user => user.Name));
        Assert.Equal(["W6-qa"], usage.Libraries[qa.Id].Select(user => user.Name));
        Assert.Equal(["W6-deploy-prod", "W6-release-fast"], usage.Libraries[prod.Id].Select(user => user.Name));
    }
}
