// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Seeding;

public sealed class DeliveryPipelineTemplateSeeder(
    AppDbContext db,
    TimeProvider timeProvider,
    IPostgresLeaderLease lease,
    ILogger<DeliveryPipelineTemplateSeeder>? logger = null,
    IAuditService? audit = null,
    IPipelineGitService? pipelineGit = null)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var organizationId = await db.Organizations
            .Where(organization => organization.Slug == DeliveryTemplateCatalog.PlatformOrganizationSlug)
            .Select(organization => organization.Id)
            .SingleAsync(ct).ConfigureAwait(false);
        // Serialize the entire catalog read and write under one advisory lock so concurrent backend
        // starts cannot insert the same template version. Read the catalog only after acquiring the
        // lock.
        if (db.Database.IsRelational())
            await lease.RunSerializedAsync(DeliveryTemplateVersionReset.LockName,
                token => SeedCatalogAsync(organizationId, token), ct).ConfigureAwait(false);
        else
            await SeedCatalogAsync(organizationId, ct).ConfigureAwait(false);
    }

    private async Task SeedCatalogAsync(int organizationId, CancellationToken ct)
    {
        // First, before anything is tracked: a colour that waited for the other one to renumber must
        // not hold the versions that colour removed (it would then see two v1 rows).
        await ResetVersionNumberingOnceAsync(organizationId, ct).ConfigureAwait(false);
        var names = DeliveryTemplateCatalog.Definitions.Keys.ToArray();
        var existing = await db.PipelineTemplates
            .Include(template => template.Versions)
            .Where(template => template.OrganizationId == organizationId && names.Contains(template.Name))
            .ToDictionaryAsync(template => template.Name, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // R-13: a generic template extending another one is pinned on the version that base holds here.
        var basePins = new GenericTemplateBasePins(existing);

        foreach (var (name, definition) in DeliveryTemplateCatalog.Definitions)
        {
            var generic = DeliveryTemplateCatalog.IsGeneric(definition.Resources);
            var versions = definition.Resources
                .Select((resource, index) => new
                {
                    Version = index + 1,
                    Yaml = generic ? basePins.DesiredYaml(name) : DeliveryTemplateCatalog.ReadResource(resource)
                })
                .ToArray();
            if (!existing.TryGetValue(name, out var template))
            {
                template = new PipelineTemplate
                {
                    Name = name,
                    Description = definition.Description,
                    Category = definition.Category,
                    OrganizationId = organizationId,
                    LatestVersion = versions.Length,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Versions = versions
                        .Select(version => new PipelineTemplateVersion
                        {
                            Version = version.Version,
                            YamlContent = version.Yaml,
                            ChangelogEntry = GetChangelogEntry(name, version.Version),
                            CreatedAt = now,
                            CreatedByUsername = "system"
                        })
                        .ToList()
                };
                db.PipelineTemplates.Add(template);
                continue;
            }

            if (generic)
            {
                var desired = versions.Single().Yaml;
                var latest = template.Versions
                    .OrderByDescending(version => version.Version)
                    .FirstOrDefault();
                if (latest is null || !string.Equals(
                        Normalize(latest.YamlContent), Normalize(desired), StringComparison.Ordinal))
                {
                    var nextVersion = Math.Max(template.LatestVersion,
                        template.Versions.Select(version => version.Version).DefaultIfEmpty(0).Max()) + 1;
                    template.Versions.Add(new PipelineTemplateVersion
                    {
                        Version = nextVersion,
                        YamlContent = desired,
                        ChangelogEntry = "Synchronized from the current reference pipeline." + basePins.ChangelogNote(name),
                        CreatedAt = now,
                        CreatedByUsername = "system"
                    });
                    template.LatestVersion = nextVersion;
                }
                template.Description = definition.Description;
                template.Category = definition.Category;
                template.UpdatedAt = now;
                continue;
            }

            foreach (var seededVersion in versions)
            {
                var version = template.Versions
                    .SingleOrDefault(item => item.Version == seededVersion.Version);
                if (version is null)
                {
                    template.Versions.Add(new PipelineTemplateVersion
                    {
                        Version = seededVersion.Version,
                        YamlContent = seededVersion.Yaml,
                        ChangelogEntry = GetChangelogEntry(name, seededVersion.Version),
                        CreatedAt = now,
                        CreatedByUsername = "system"
                    });
                }
                else if (!string.Equals(
                             Normalize(version.YamlContent),
                             Normalize(seededVersion.Yaml),
                             StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Seeded delivery template '{name}@{seededVersion.Version}' differs from its immutable published version.");
                }
            }
            template.Description = definition.Description;
            template.Category = definition.Category;
            template.LatestVersion = Math.Max(template.LatestVersion, versions.Length);
            template.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs the one-time shipped-template reset while holding the catalog lock from <see
    /// cref="SeedAsync"/>. Recheck the marker under that lock so concurrent startup does not repeat
    /// the reset.</summary>
    private async Task ResetVersionNumberingOnceAsync(int organizationId, CancellationToken ct)
    {
        // Use the first shipped file as v1 and pass all shipped files so templates already matching
        // them remain untouched.
        var shipped = DeliveryTemplateCatalog.Definitions.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.Resources.Select(DeliveryTemplateCatalog.ReadResource).ToArray(),
            StringComparer.Ordinal);
        var reset = new DeliveryTemplateVersionReset(
            db, timeProvider, logger ?? (ILogger)NullLogger.Instance, audit, pipelineGit);
        await reset.RunAsync(organizationId, shipped, ct).ConfigureAwait(false);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    // Keep each shipped version changelog beside the version it describes.
    private static string GetChangelogEntry(string name, int version) => (name, version) switch
    {
        (_, 1) => "Initial reusable application delivery contract.",
        ("application-candidate", 2) =>
            "Releases the generic CI payload (ApplicationPayload-artifacts), skips the quality, security and QA "
            + "stages of pipelines the project does not have, and lets the baseline restore export its own identity (R2-121).",
        ("application-candidate", 3) =>
            "Takes the stage order of the platform's own candidate: deployed baseline in service, bootstrap check, "
            + "CI, Quality, Security then QA, release; no assurance seal (R2-1, ADR-054).",
        ("application-candidate", 4) =>
            "Grades the release from its Quality and Security gates (grade-candidate-from-gates): the release carries "
            + "the worst gate grade and whether it reaches the deployment threshold, so an accepted release graded A to E "
            + "deploys without the approval a refused, F or ungraded one still asks for (R-23, R-24).",
        ("application-candidate", 5) =>
            "Names the release with a real version instead of c-<commit> (R-33): <APPLICATION_VERSION_PREFIX>.<n> allocated by "
            + "Aetheus (release_number: allocate, prefix 0.1 by default), or the version the run parameter applicationVersion "
            + "gives; a version already published is refused at the launch. The CI it triggers receives it as APP_VERSION.",
        ("host-bluegreen-deploy", 2) =>
            "Drops the required `revision` run parameter: the journal revision defaults to the commit the run was "
            + "launched on and a consumer overrides BG_REVISION; an unresolved revision is refused before the "
            + "schema migrates (R-25).",
        _ => $"Published delivery template version {version}."
    };
}
