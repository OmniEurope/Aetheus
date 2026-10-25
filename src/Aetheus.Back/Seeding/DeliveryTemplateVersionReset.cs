// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Seeding;

/// <summary>Normalizes eligible shipped templates to their first shipped version once under a
/// transactional marker, preserving other organisations, user templates and templates already
/// matching the shipped files. Git repinning runs separately under the catalog lock using the
/// recorded reset template names and its own completion marker, so retries cannot repin newer
/// catalog versions.</summary>
internal sealed partial class DeliveryTemplateVersionReset(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger logger,
    IAuditService? audit,
    IPipelineGitService? pipelineGit = null)
{
    /// <summary>The completion marker for this reset.</summary>
    internal const string MarkerKey = "DeliveryTemplates:VersionReset:R1-20";

    /// <summary>The compatibility reset marker, written beside <see cref="MarkerKey"/> when
    /// missing.</summary>
    internal const string FirstResetMarkerKey = "DeliveryTemplates:VersionReset:R2-094";

    /// <summary>The renumbered template names (comma-separated), written with <see cref="MarkerKey"/>.</summary>
    internal const string GitRepinTemplatesKey = MarkerKey + ":GitRepinTemplates";

    /// <summary>Written once every Git definition extending a renumbered template is at <c>@1</c>.</summary>
    internal const string GitRepinDoneKey = MarkerKey + ":GitRepinDone";

    /// <summary>The advisory lock two backend colours starting together serialize on.</summary>
    internal const string LockName = "delivery-template-version-reset";

    internal const string ChangelogEntry = "Version numbering restarted at v1 with the current content (recette R1-20).";

    private const string Recette = "R1-20";
    private const string GitActor = "system";

    /// <param name="shippedYamlByName">Every shipped file of each template, in version order (v1 first).</param>
    public async Task RunAsync(
        int organizationId, IReadOnlyDictionary<string, IReadOnlyList<string>> shippedYamlByName, CancellationToken ct)
    {
        if (await db.AppSettings.AnyAsync(setting => setting.Key == MarkerKey, ct).ConfigureAwait(false))
        {
            await RetryGitRepinAsync(organizationId, ct).ConfigureAwait(false);
            return;
        }

        List<(PipelineTemplate Template, int PreviousLatest)> renumbered;
        List<(Pipeline Pipeline, int? PreviousVersion)> repinned;
        List<Pipeline> referencing;
        var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        await using (transaction)
        {
            renumbered = await RenumberTemplatesAsync(organizationId, shippedYamlByName, ct).ConfigureAwait(false);
            (referencing, repinned) = await RepinPipelinesAsync(organizationId, renumbered, ct).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (!await db.AppSettings.AnyAsync(setting => setting.Key == FirstResetMarkerKey, ct).ConfigureAwait(false))
                db.AppSettings.Add(new AppSetting { Key = FirstResetMarkerKey, Value = now });
            db.AppSettings.Add(new AppSetting { Key = MarkerKey, Value = now });
            // Nothing renumbered means no Git file to repin: the Git pass is done with the reset.
            db.AppSettings.Add(renumbered.Count == 0
                ? new AppSetting { Key = GitRepinDoneKey, Value = now }
                : new AppSetting
                {
                    Key = GitRepinTemplatesKey,
                    Value = string.Join(',', renumbered.Select(item => item.Template.Name))
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
                "Delivery template {Template} renumbered: v1..v{Previous} replaced by a single v1 holding the shipped content ({Recette})",
                template.Name, previousLatest, Recette);
            if (audit is not null)
                await audit.LogAsync("TemplateVersionsReset", "PipelineTemplate", template.Id,
                    $"{template.Name}: v{previousLatest} -> v1", ct).ConfigureAwait(false);
        }
        foreach (var (pipeline, previousVersion) in repinned)
        {
            logger.LogWarning(
                "Pipeline {Pipeline} ({PipelineId}) repinned from {Template}@{Previous} to @1 ({Recette})",
                pipeline.Name, pipeline.Id, pipeline.TemplateReferenceName, previousVersion, Recette);
            if (audit is not null)
                await audit.LogAsync("TemplateVersionRepinned", "Pipeline", pipeline.Id,
                    $"{pipeline.TemplateReferenceName}: v{previousVersion} -> v1", ct).ConfigureAwait(false);
        }
        var gitRepinned = 0;
        if (renumbered.Count > 0)
        {
            var names = renumbered.Select(item => item.Template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            (gitRepinned, var complete) = await RepinGitDefinitionsAsync(names, referencing, ct).ConfigureAwait(false);
            if (complete)
                await MarkGitRepinDoneAsync(ct).ConfigureAwait(false);
        }
        logger.LogInformation(
            "Delivery template version reset ({Recette}) done: {Templates} template(s) renumbered, {Pipelines} pipeline(s) repinned, {GitFiles} Git definition(s) repinned",
            Recette, renumbered.Count, repinned.Count, gitRepinned);
    }

    private async Task<List<(PipelineTemplate Template, int PreviousLatest)>> RenumberTemplatesAsync(
        int organizationId, IReadOnlyDictionary<string, IReadOnlyList<string>> shippedYamlByName, CancellationToken ct)
    {
        var names = shippedYamlByName.Keys.ToArray();
        var templates = await db.PipelineTemplates
            .Include(template => template.Versions)
            .Where(template => template.OrganizationId == organizationId && names.Contains(template.Name))
            .ToListAsync(ct).ConfigureAwait(false);
        var renumbered = new List<(PipelineTemplate, int)>();
        foreach (var template in templates.Where(template =>
                     (template.LatestVersion != 1 || template.Versions.Any(version => version.Version != 1))
                     && !StoresExactlyTheShippedFiles(template, shippedYamlByName[template.Name])))
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
                YamlContent = shippedYamlByName[template.Name][0],
                ChangelogEntry = ChangelogEntry,
                CreatedAt = now,
                CreatedByUsername = "system"
            });
            template.LatestVersion = 1;
            template.UpdatedAt = now;
        }
        return renumbered;
    }

    /// <summary>True when the template holds v1..vN with the N shipped files' content, nothing more.</summary>
    private static bool StoresExactlyTheShippedFiles(PipelineTemplate template, IReadOnlyList<string> shipped)
    {
        var stored = template.Versions.OrderBy(version => version.Version).ToList();
        return template.LatestVersion == shipped.Count
            && stored.Count == shipped.Count
            && stored.Select((version, index) => version.Version == index + 1
                && string.Equals(Normalize(version.YamlContent), Normalize(shipped[index]), StringComparison.Ordinal))
                .All(matches => matches);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private async Task<(List<Pipeline> Referencing, List<(Pipeline Pipeline, int? PreviousVersion)> Repinned)> RepinPipelinesAsync(
        int organizationId, List<(PipelineTemplate Template, int PreviousLatest)> renumbered, CancellationToken ct)
    {
        if (renumbered.Count == 0) return ([], []);
        var pipelines = await LoadReferencingPipelinesAsync(
            organizationId, renumbered.Select(item => item.Template.Name), ct).ConfigureAwait(false);
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
        return (pipelines, repinned);
    }

    private Task<List<Pipeline>> LoadReferencingPipelinesAsync(
        int organizationId, IEnumerable<string> templateNames, CancellationToken ct)
    {
        var names = templateNames.Select(name => name.ToLower()).ToArray();
        return db.Pipelines
            .Where(pipeline => pipeline.TemplateReferenceName != null
                && names.Contains(pipeline.TemplateReferenceName.ToLower())
                && (pipeline.Project != null && pipeline.Project.OrganizationId == organizationId
                    || pipeline.Environment != null && pipeline.Environment.Project != null
                        && pipeline.Environment.Project.OrganizationId == organizationId
                    || pipeline.ProjectServer != null
                        && pipeline.ProjectServer.Project.OrganizationId == organizationId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// A start after the reset: runs the Git pass again while <see cref="GitRepinDoneKey"/> is missing,
    /// on the template names the reset recorded. A database whose reset predates that record (only a
    /// local one, the record shipped with the reset's first deployment) has nothing to retry.
    /// </summary>
    private async Task RetryGitRepinAsync(int organizationId, CancellationToken ct)
    {
        if (await db.AppSettings.AnyAsync(setting => setting.Key == GitRepinDoneKey, ct).ConfigureAwait(false))
            return;
        var recorded = await db.AppSettings
            .Where(setting => setting.Key == GitRepinTemplatesKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var names = (recorded ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            logger.LogWarning(
                "Delivery template version reset ({Recette}): no renumbered template recorded, the Git repin is marked done",
                Recette);
            await MarkGitRepinDoneAsync(ct).ConfigureAwait(false);
            return;
        }
        if (pipelineGit is null) return;
        var referencing = await LoadReferencingPipelinesAsync(organizationId, names, ct).ConfigureAwait(false);
        var (committed, complete) = await RepinGitDefinitionsAsync(names, referencing, ct).ConfigureAwait(false);
        if (complete)
            await MarkGitRepinDoneAsync(ct).ConfigureAwait(false);
        logger.LogInformation(
            "Delivery template version reset ({Recette}): Git repin retried, {GitFiles} Git definition(s) repinned, complete: {Complete}",
            Recette, committed, complete);
    }

    private async Task MarkGitRepinDoneAsync(CancellationToken ct)
    {
        db.AppSettings.Add(new AppSetting
        {
            Key = GitRepinDoneKey,
            Value = timeProvider.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A project-owned pipeline's run reads its <c>.pipeline/</c> file before the database
    /// (<c>PipelineRunPreparationService.ResolveDefinitionYamlAsync</c>): left at <c>@6</c>, it would
    /// resolve a version the reset removed. The file is read and repinned on its own (it may differ from
    /// the database row), only when its <c>extends:</c> names a renumbered template. Git is not part of
    /// the database transaction: a failed write is logged as an error for that pipeline and never stops
    /// the start; <c>Complete</c> is then false and the next start retries (<see cref="GitRepinDoneKey"/>).
    /// </summary>
    private async Task<(int Committed, bool Complete)> RepinGitDefinitionsAsync(
        IReadOnlySet<string> names, List<Pipeline> referencing, CancellationToken ct)
    {
        if (pipelineGit is null) return (0, false);
        var committed = 0;
        var complete = true;
        foreach (var pipeline in referencing)
        {
            if (pipeline.ProjectId is not { } projectId) continue;
            try
            {
                var gitYaml = await pipelineGit.ReadProjectPipelineYamlAsync(
                    projectId, pipeline.Name, ct, pipeline.SourceBranch, pipeline.SourceRepositoryId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(gitYaml)) continue;
                var repinnedYaml = RepinExtends(gitYaml, names);
                if (string.Equals(repinnedYaml, gitYaml, StringComparison.Ordinal)) continue;
                var (outcome, error) = await pipelineGit.WriteProjectPipelineYamlAsync(
                    projectId, pipeline.Name, repinnedYaml, GitActor, ct,
                    pipeline.SourceBranch, pipeline.SourceRepositoryId).ConfigureAwait(false);
                if (outcome != GitWriteOutcome.Committed)
                {
                    logger.LogError(
                        "Pipeline {Pipeline} ({PipelineId}): its Git definition could not be repinned to @1 ({Recette}): {Error}",
                        pipeline.Name, pipeline.Id, Recette, error ?? outcome.ToString());
                    complete = false;
                    continue;
                }
                committed++;
                logger.LogWarning("Pipeline {Pipeline} ({PipelineId}): Git definition repinned to @1 ({Recette})",
                    pipeline.Name, pipeline.Id, Recette);
                if (audit is not null)
                    await audit.LogAsync("TemplateVersionRepinned", "Pipeline", pipeline.Id,
                        $"{pipeline.TemplateReferenceName}: Git definition -> v1", ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Mandatory: the database reset is committed; one unreadable repository must not stop
                // the backend start nor the other pipelines' files. The next start retries it.
                logger.LogError(exception,
                    "Pipeline {Pipeline} ({PipelineId}): its Git definition could not be repinned to @1 ({Recette})",
                    pipeline.Name, pipeline.Id, Recette);
                complete = false;
            }
        }
        return (committed, complete);
    }

    /// <summary>Rewrites the version of the first <c>extends: name@N</c> line to 1, keeping quotes and comment.</summary>
    internal static string RepinExtends(string yaml) =>
        ExtendsVersion().Replace(yaml, match => match.Groups["head"].Value + "1", 1);

    /// <summary>As <see cref="RepinExtends(string)"/>, only when that line extends one of <paramref name="templateNames"/>.</summary>
    internal static string RepinExtends(string yaml, IReadOnlySet<string> templateNames)
    {
        var match = ExtendsVersion().Match(yaml);
        return match.Success && templateNames.Contains(match.Groups["name"].Value.Trim())
            ? RepinExtends(yaml)
            : yaml;
    }

    [GeneratedRegex(@"^(?<head>[ \t]*extends[ \t]*:[ \t]*['""]?(?<name>[^@'""#\r\n]+)@)\d+", RegexOptions.Multiline)]
    private static partial Regex ExtendsVersion();
}
