// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Data;

/// <summary>
/// Recette R2-094, decided on 2026-10-04 (option a): every delivery template the platform ships
/// restarts at v1 holding its current content, and every pipeline extending one is pinned to @1.
/// <para>
/// Runs once per database. A template of the platform's organization that stores any version other
/// than v1 loses all its versions and gets a single v1 with the shipped content; the pipelines of that
/// organization referencing it get <c>TemplateReferenceVersion = 1</c> and their <c>extends:</c> line
/// repinned to <c>@1</c>. User-made templates (any other name) and other organizations are untouched.
/// </para>
/// <para>
/// The one-time guarantee is the <see cref="MarkerKey"/> row, written in the same transaction:
/// the generic templates grow a version each time their file changes, so "more versions than shipped
/// files" alone would renumber them again at every such change and turn v1 into a mutable version.
/// After the marker, the seeder's normal immutability check is the only rule.
/// </para>
/// </summary>
internal sealed partial class DeliveryTemplateVersionReset(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger logger,
    IAuditService? audit)
{
    internal const string MarkerKey = "DeliveryTemplates:VersionReset:R2-094";

    /// <summary>The advisory lock two backend colours starting together serialize on.</summary>
    internal const string LockName = "delivery-template-version-reset";

    internal const string ChangelogEntry = "Version numbering restarted at v1 with the current content (recette R2-094).";

    public async Task RunAsync(
        int organizationId, IReadOnlyDictionary<string, string> shippedYamlByName, CancellationToken ct)
    {
        if (await db.AppSettings.AnyAsync(setting => setting.Key == MarkerKey, ct).ConfigureAwait(false))
            return;

        List<(PipelineTemplate Template, int PreviousLatest)> renumbered;
        List<(Pipeline Pipeline, int? PreviousVersion)> repinned;
        var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        await using (transaction)
        {
            renumbered = await RenumberTemplatesAsync(organizationId, shippedYamlByName, ct).ConfigureAwait(false);
            repinned = await RepinPipelinesAsync(organizationId, renumbered, ct).ConfigureAwait(false);
            db.AppSettings.Add(new AppSetting
            {
                Key = MarkerKey,
                Value = timeProvider.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        // Audited once committed: the audit chain retries on a concurrent writer, which a failed
        // statement inside this transaction would have aborted.
        foreach (var (template, previousLatest) in renumbered)
        {
            logger.LogWarning(
                "Delivery template {Template} renumbered: v1..v{Previous} replaced by a single v1 holding the shipped content (R2-094)",
                template.Name, previousLatest);
            if (audit is not null)
                await audit.LogAsync("TemplateVersionsReset", "PipelineTemplate", template.Id,
                    $"{template.Name}: v{previousLatest} -> v1", ct).ConfigureAwait(false);
        }
        foreach (var (pipeline, previousVersion) in repinned)
        {
            logger.LogWarning(
                "Pipeline {Pipeline} ({PipelineId}) repinned from {Template}@{Previous} to @1 (R2-094)",
                pipeline.Name, pipeline.Id, pipeline.TemplateReferenceName, previousVersion);
            if (audit is not null)
                await audit.LogAsync("TemplateVersionRepinned", "Pipeline", pipeline.Id,
                    $"{pipeline.TemplateReferenceName}: v{previousVersion} -> v1", ct).ConfigureAwait(false);
        }
        logger.LogInformation(
            "Delivery template version reset done: {Templates} template(s) renumbered, {Pipelines} pipeline(s) repinned",
            renumbered.Count, repinned.Count);
    }

    private async Task<List<(PipelineTemplate Template, int PreviousLatest)>> RenumberTemplatesAsync(
        int organizationId, IReadOnlyDictionary<string, string> shippedYamlByName, CancellationToken ct)
    {
        var names = shippedYamlByName.Keys.ToArray();
        var templates = await db.PipelineTemplates
            .Include(template => template.Versions)
            .Where(template => template.OrganizationId == organizationId && names.Contains(template.Name))
            .ToListAsync(ct).ConfigureAwait(false);
        var renumbered = new List<(PipelineTemplate, int)>();
        foreach (var template in templates.Where(template =>
                     template.LatestVersion != 1 || template.Versions.Any(version => version.Version != 1)))
        {
            renumbered.Add((template, Math.Max(template.LatestVersion,
                template.Versions.Select(version => version.Version).DefaultIfEmpty(0).Max())));
            // Published versions are never modified (AppDbContext refuses it): they are removed, and
            // the new v1 is inserted after the delete is flushed so the (TemplateId, Version) unique
            // index never sees two v1 rows.
            db.PipelineTemplateVersions.RemoveRange(template.Versions.ToList());
        }
        if (renumbered.Count == 0) return renumbered;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (template, _) in renumbered)
        {
            template.Versions.Clear();
            template.Versions.Add(new PipelineTemplateVersion
            {
                Version = 1,
                YamlContent = shippedYamlByName[template.Name],
                ChangelogEntry = ChangelogEntry,
                CreatedAt = now,
                CreatedByUsername = "system"
            });
            template.LatestVersion = 1;
            template.UpdatedAt = now;
        }
        return renumbered;
    }

    private async Task<List<(Pipeline Pipeline, int? PreviousVersion)>> RepinPipelinesAsync(
        int organizationId, List<(PipelineTemplate Template, int PreviousLatest)> renumbered, CancellationToken ct)
    {
        if (renumbered.Count == 0) return [];
        var names = renumbered.Select(item => item.Template.Name.ToLower()).ToArray();
        var pipelines = await db.Pipelines
            .Where(pipeline => pipeline.TemplateReferenceName != null
                && names.Contains(pipeline.TemplateReferenceName.ToLower())
                && (pipeline.Project != null && pipeline.Project.OrganizationId == organizationId
                    || pipeline.Environment != null && pipeline.Environment.Project != null
                        && pipeline.Environment.Project.OrganizationId == organizationId
                    || pipeline.ProjectServer != null
                        && pipeline.ProjectServer.Project.OrganizationId == organizationId))
            .ToListAsync(ct).ConfigureAwait(false);
        var repinned = new List<(Pipeline, int?)>();
        foreach (var pipeline in pipelines)
        {
            var yaml = RepinExtends(pipeline.YamlDefinition);
            if (pipeline.TemplateReferenceVersion == 1 && string.Equals(yaml, pipeline.YamlDefinition, StringComparison.Ordinal))
                continue;
            repinned.Add((pipeline, pipeline.TemplateReferenceVersion));
            pipeline.YamlDefinition = yaml;
            pipeline.TemplateReferenceVersion = 1;
        }
        return repinned;
    }

    /// <summary>Rewrites the version of the first <c>extends: name@N</c> line to 1, keeping quotes and comment.</summary>
    internal static string RepinExtends(string yaml) =>
        ExtendsVersion().Replace(yaml, match => match.Groups["head"].Value + "1", 1);

    [GeneratedRegex(@"^(?<head>[ \t]*extends[ \t]*:[ \t]*['""]?[^@'""#\r\n]+@)\d+", RegexOptions.Multiline)]
    private static partial Regex ExtendsVersion();
}
