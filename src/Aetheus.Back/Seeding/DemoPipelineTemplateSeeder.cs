// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Seeding;

/// <summary>Reconciles the pipeline-template portion of the dated local/QA demo dataset.</summary>
internal static class DemoPipelineTemplateSeeder
{
    public static async Task SeedAsync(AppDbContext db, int organizationId)
    {
        var templates = new[]
        {
            Create("Demo Web Delivery", "Reusable build and deployment flow for the demo web application.",
                "Demo CI/CD", "template-web", organizationId),
            Create("Demo API Validation", "Reusable validation flow for the demo internal API.",
                "Demo Quality", "template-api", organizationId),
            Create("Demo Toto Release", "Reusable QA release flow for the Toto rollback scenario.",
                "Demo Release", "template-toto", organizationId)
        };

        var names = templates.Select(template => template.Name).ToArray();
        var existingNames = await db.PipelineTemplates
            .Where(template => template.OrganizationId == organizationId && names.Contains(template.Name))
            .Select(template => template.Name)
            .ToListAsync().ConfigureAwait(false);

        db.PipelineTemplates.AddRange(templates.Where(template =>
            !existingNames.Contains(template.Name, StringComparer.OrdinalIgnoreCase)));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static PipelineTemplate Create(
        string name, string description, string category, string slug, int organizationId) => new()
        {
            Name = name,
            Description = description,
            Category = category,
            OrganizationId = organizationId,
            YamlContent = BuildYaml(slug)
        };

    // The demo commands perform and verify real file work when a seeded template is executed.
    private static string BuildYaml(string slug) => $"""
        name: demo-{slug}
        trigger: manual
        stages:
          - name: build
            steps:
              - name: compile
                shell: mkdir -p dist && printf 'demo-{slug}\n' > dist/index.html && test -s dist/index.html
          - name: deploy
            depends_on: [build]
            steps:
              - name: verify
                shell: test -s dist/index.html && mkdir -p deployed && cp dist/index.html deployed/index.html && cmp dist/index.html deployed/index.html
        """;
}
