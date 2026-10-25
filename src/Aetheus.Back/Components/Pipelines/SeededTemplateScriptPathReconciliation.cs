// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// One-shot, idempotent startup reconciliation. <see cref="DbInitializer"/> seeds the example pipeline templates only into
/// an empty database, so an instance seeded before <c>normalize-coverage-report.sh</c> moved from <c>deploy/scripts/</c> to
/// <c>scripts/</c> keeps the old path in two of them. Published template versions are immutable, so the fix is a new
/// version of each affected template, never an edit.
/// Only a seeded template whose latest version is still the one the seeder (or this pass) wrote is touched: the seeded
/// identity is the name and category of the example, and the author of the latest version must be
/// <see cref="SystemAuthor"/>. A template a person has edited since (their version is the latest) or created themselves is
/// never rewritten. Only the exact old script path is replaced.
/// </summary>
public static class SeededTemplateScriptPathReconciliation
{
    internal const string SystemAuthor = "system";
    internal const string SeededCategory = "CI/CD";
    internal const string OldScriptPath = "$(WORKSPACE)/deploy/scripts/normalize-coverage-report.sh";
    internal const string NewScriptPath = "$(WORKSPACE)/scripts/normalize-coverage-report.sh";
    internal const string Changelog = "Moved normalize-coverage-report.sh from deploy/scripts to scripts";

    /// <summary>The names of the seeded example templates that call the coverage normalizer.</summary>
    internal static readonly string[] SeededTemplateNames = ["Build=VPS, Deploy=VPS", "Build=VPS, Deploy=Windows"];

    public static async Task RunAsync(AppDbContext db, TimeProvider timeProvider, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        // Tracked query: the new versions are added to the tracked templates and persisted by one SaveChanges.
        var templates = await db.PipelineTemplates
            .Where(template => template.Category == SeededCategory && SeededTemplateNames.Contains(template.Name))
            .Include(template => template.Versions)
            .ToListAsync(ct).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var republished = 0;
        foreach (var template in templates)
        {
            var latest = template.Versions.OrderByDescending(version => version.Version).FirstOrDefault();
            if (latest is null
                || latest.CreatedByUsername != SystemAuthor
                || !latest.YamlContent.Contains(OldScriptPath, StringComparison.Ordinal))
                continue;

            template.LatestVersion = latest.Version + 1;
            template.UpdatedAt = now;
            template.Versions.Add(new PipelineTemplateVersion
            {
                Version = template.LatestVersion,
                YamlContent = latest.YamlContent.Replace(OldScriptPath, NewScriptPath, StringComparison.Ordinal),
                ChangelogEntry = Changelog,
                CreatedAt = now,
                CreatedByUsername = SystemAuthor
            });
            republished++;
        }

        if (republished > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Published a new version of {Count} seeded pipeline template(s) to point at scripts/normalize-coverage-report.sh.",
                republished);
        }
    }
}
