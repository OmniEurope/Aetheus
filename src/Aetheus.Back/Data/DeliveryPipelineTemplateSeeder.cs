// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Data;

public sealed class DeliveryPipelineTemplateSeeder(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<DeliveryPipelineTemplateSeeder>? logger = null,
    IAuditService? audit = null,
    IPostgresLeaderLease? lease = null,
    IPipelineGitService? pipelineGit = null)
{
    private const string ResourceRoot = "DeliveryPipelineTemplates/";

    /// <summary>The organization the shipped templates are seeded in; every other organization extends
    /// them from there (recette R2-121).</summary>
    internal const string PlatformOrganizationSlug = "aetheus";

    internal static readonly IReadOnlyDictionary<string, (string Description, string Category, string[] Resources)> Definitions =
        new Dictionary<string, (string, string, string[])>(StringComparer.Ordinal)
        {
            ["application-light"] = (
                "Reusable differential feedback orchestration delegating project commands to a versioned adapter.",
                "Application Delivery",
                ["application-light-v1.yaml"]),
            ["application-candidate"] = (
                "Reusable build-once candidate orchestration with production baseline, QA and immutable release.",
                "Application Delivery",
                ["application-candidate-v1.yaml", "application-candidate-v2.yaml"]),
            ["application-promotion"] = (
                "Reusable promotion orchestration deploying and verifying an already-qualified candidate.",
                "Application Delivery",
                ["application-promotion-v1.yaml"]),
            ["application-ci"] = (
                "Run the project's CI adapter and package its immutable application payload.",
                "Application Delivery",
                ["generic/application-ci.yaml"]),
            ["application-quality"] = (
                "Run the project's quality adapter, then publish and grade coverage, lint and complexity evidence for any language.",
                "Quality",
                ["generic/application-quality.yaml"]),
            ["application-security"] = (
                "Scan secrets, code, dependencies and infrastructure files with the platform's scanners and grade the findings.",
                "Security",
                ["generic/application-security.yaml"]),
            ["application-security-external"] = (
                "Scan code, dependencies and infrastructure files of an external repository with the platform's scanners and grade the findings, with no file required in that repository.",
                "Security",
                ["generic/application-security-external.yaml"]),
            ["application-qa"] = (
                "Run the project's QA adapter against the immutable application payload.",
                "Quality",
                ["generic/application-qa.yaml"]),
            ["application-qa-extended"] = (
                "Run the project's extended QA adapter, the nightly's heavy validation, against the immutable application payload.",
                "Quality",
                ["generic/application-qa-extended.yaml"]),
            ["application-security-history"] = (
                "Scan the repository's whole Git history for secrets and grade it, the nightly extension of the security pipeline.",
                "Security",
                ["generic/application-security-history.yaml"]),
            ["application-nightly"] = (
                "Scheduled complete assurance and drift checks.",
                "Quality",
                ["generic/application-nightly.yaml"]),
            ["host-bluegreen-deploy"] = (
                "Reusable blue-green host cutover built from native step types, with an advisory evidence stage and an automatic rollback.",
                "Application Delivery",
                ["host-bluegreen-deploy-v1.yaml"]),
            ["host-bluegreen-revert"] = (
                "Return traffic to the colour a blue-green commit kept in reserve (N-1), in seconds.",
                "Application Delivery",
                ["host-bluegreen-revert-v1.yaml"]),
            ["application-deploy-prod"] = (
                "Restore an immutable candidate and run the project's production deployment adapter.",
                "Application Delivery",
                ["generic/application-deploy-prod.yaml"]),
            ["application-release-fast"] = (
                "Run the project's explicitly opted-in fast release adapter.",
                "Application Delivery",
                ["generic/application-release-fast.yaml"]),
            ["package-telemetry"] = (
                "Build and qualify the project's telemetry package through its adapter.",
                "Packages",
                ["generic/package-telemetry.yaml"]),
            ["package-web-analytics-dotnet"] = (
                "Build and qualify the project's .NET web analytics package through its adapter.",
                "Packages",
                ["generic/package-web-analytics-dotnet.yaml"]),
            ["package-web-analytics-browser"] = (
                "Build and qualify the project's browser analytics package through its adapter.",
                "Packages",
                ["generic/package-web-analytics-browser.yaml"]),
            ["publish-observability-packages"] = (
                "Build the selected package set and run the project's publication adapter.",
                "Packages",
                ["generic/publish-observability-packages.yaml"])
        };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var organizationId = await db.Organizations
            .Where(organization => organization.Slug == PlatformOrganizationSlug)
            .Select(organization => organization.Id)
            .SingleAsync(ct).ConfigureAwait(false);
        // Two backend colours can start together. The whole catalog write runs under one advisory
        // lock, not only the R2-094 reset: a version a release adds (application-candidate v2,
        // R2-121) was otherwise inserted by both colours, and the second insert broke the
        // (TemplateId, Version) unique index and the start of that colour. The colour that waited
        // reads the catalog only once it holds the lock, so it finds the work done.
        if (lease is not null && db.Database.IsRelational())
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
        await ArchiveLegacyCatalogAsync(organizationId, ct).ConfigureAwait(false);
        var names = Definitions.Keys.ToArray();
        var existing = await db.PipelineTemplates
            .Include(template => template.Versions)
            .Where(template => template.OrganizationId == organizationId && names.Contains(template.Name))
            .ToDictionaryAsync(template => template.Name, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var (name, definition) in Definitions)
        {
            var versions = definition.Resources
                .Select((resource, index) => new
                {
                    Version = index + 1,
                    Yaml = ReadResource(resource)
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

            if (definition.Resources.All(resource =>
                    resource.StartsWith("generic/", StringComparison.Ordinal)))
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
                        ChangelogEntry = "Synchronized from the current reference pipeline.",
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

    /// <summary>
    /// Recettes R2-094 then R1-20: the one-time restart of every shipped template at v1. It runs under
    /// the catalog lock <see cref="SeedAsync"/> holds and re-reads its marker there: the second of two
    /// colours starting together finds the work done and changes nothing.
    /// </summary>
    private async Task ResetVersionNumberingOnceAsync(int organizationId, CancellationToken ct)
    {
        // The first shipped file is v1. With one file per template that was also the last; once a
        // template ships a v2 (application-candidate, R2-121), renumbering a database that never ran the
        // reset must still give v1 the v1 content, or the seeding below would refuse its own v1. Every
        // file is passed so a template already holding exactly its shipped files is left alone.
        var shipped = Definitions.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.Resources.Select(ReadResource).ToArray(),
            StringComparer.Ordinal);
        var reset = new DeliveryTemplateVersionReset(
            db, timeProvider, logger ?? (ILogger)NullLogger.Instance, audit, pipelineGit);
        await reset.RunAsync(organizationId, shipped, ct).ConfigureAwait(false);
    }

    internal static string ReadResource(string fileName)
    {
        var assembly = typeof(DeliveryPipelineTemplateSeeder).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{ResourceRoot}{fileName}")
            ?? throw new InvalidOperationException(
                $"Embedded delivery pipeline template '{fileName}' is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task ArchiveLegacyCatalogAsync(int organizationId, CancellationToken ct)
    {
        var canonicalNames = Definitions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var templates = await db.PipelineTemplates
            .Include(template => template.Versions)
            .Where(template => template.OrganizationId == organizationId)
            .ToListAsync(ct).ConfigureAwait(false);
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.Project != null
                && pipeline.Project.OrganizationId == organizationId)
            .Select(pipeline => new { pipeline.TemplateReferenceName })
            .ToListAsync(ct).ConfigureAwait(false);
        var allVersionYaml = templates.SelectMany(template => template.Versions)
            .Select(version => version.YamlContent)
            .ToArray();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var template in templates.Where(template =>
                     !canonicalNames.Contains(template.Name)
                     && !template.Name.StartsWith("legacy-", StringComparison.OrdinalIgnoreCase)))
        {
            var legacyName = LegacyName(template.Name);
            if (!templates.Any(existing =>
                    string.Equals(existing.Name, legacyName, StringComparison.OrdinalIgnoreCase)))
            {
                db.PipelineTemplates.Add(new PipelineTemplate
                {
                    Name = legacyName,
                    Description = $"Archived snapshot of '{template.Name}'. {template.Description}".Trim(),
                    Category = "Legacy",
                    OrganizationId = organizationId,
                    LatestVersion = template.LatestVersion,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Versions = template.Versions.Select(version => new PipelineTemplateVersion
                    {
                        Version = version.Version,
                        YamlContent = version.YamlContent,
                        ChangelogEntry = $"Legacy snapshot: {version.ChangelogEntry}",
                        CreatedAt = now,
                        CreatedByUsername = "system"
                    }).ToList()
                });
            }

            var referenced = pipelines.Any(pipeline => string.Equals(
                    pipeline.TemplateReferenceName, template.Name, StringComparison.OrdinalIgnoreCase))
                || allVersionYaml.Any(yaml => ReferencesTemplate(yaml, template.Name));
            if (referenced)
            {
                if (!string.Equals(template.Category, "Legacy reference", StringComparison.Ordinal))
                {
                    template.Category = "Legacy reference";
                    template.Description = $"Retained until references migrate to '{legacyName}'. {template.Description}";
                    template.UpdatedAt = now;
                }
            }
            else
            {
                db.PipelineTemplates.Remove(template);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static bool ReferencesTemplate(string yaml, string templateName)
    {
        var marker = $"extends: {templateName}";
        return yaml.Split('\n').Any(line =>
            line.Trim().StartsWith(marker, StringComparison.OrdinalIgnoreCase)
            && (line.Trim().Length == marker.Length
                || line.Trim()[marker.Length] is '@' or ' ' or '\r'));
    }

    private static string LegacyName(string name)
    {
        var slug = new string(name.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return $"legacy-{slug.Trim('-')}";
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    // Recette R2-094: every shipped template restarted at v1. A v2 file added later gets its own
    // changelog line here, beside the version it describes.
    private static string GetChangelogEntry(string name, int version) => (name, version) switch
    {
        (_, 1) => "Initial reusable application delivery contract.",
        ("application-candidate", 2) =>
            "Releases the generic CI payload (ApplicationPayload-artifacts), skips the quality, security and QA "
            + "stages of pipelines the project does not have, and lets the baseline restore export its own identity (R2-121).",
        _ => $"Published delivery template version {version}."
    };
}
