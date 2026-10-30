// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineFleetService(
    IPipelineFleetRepository fleetRepository,
    IPipelineTemplateRepository pipelineRepository,
    IPipelineService pipelineService,
    IPipelineTemplateService templateService,
    IPipelineTemplateResolver templateResolver,
    IAuditService audit,
    IDbTransactionScope? transaction = null) : IPipelineFleetService
{
    private static readonly Regex ExtendsLinePattern = new(
        @"^(?<prefix>\s*extends\s*:\s*)(?<reference>[^#\r\n]+)(?<suffix>\s*(?:#.*)?)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<PaginatedResult<PipelineFleetItemDto>> GetAsync(
        PipelineFleetPaginationRequest request,
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        CancellationToken ct = default)
    {
        return await fleetRepository.GetPageAsync(
            request, organizationIds, accessiblePipelineIds, ct).ConfigureAwait(false);
    }

    public async Task<PipelineFleetFilterValuesDto> GetFilterValuesAsync(
        IReadOnlyCollection<int>? organizationIds,
        IReadOnlyCollection<int>? accessiblePipelineIds,
        CancellationToken ct = default) =>
        new()
        {
            Templates = await fleetRepository.GetTemplateNamesAsync(organizationIds, accessiblePipelineIds, ct).ConfigureAwait(false)
        };

    public async Task<PipelineFleetItemDto> GetItemAsync(int pipelineId, CancellationToken ct = default)
    {
        var row = await GetRowAsync(pipelineId, ct).ConfigureAwait(false);
        var reference = ParseReference(row.YamlDefinition);
        var template = reference is null
            ? null
            : await FindTemplateAsync(reference.Name, row.OrganizationId, ct).ConfigureAwait(false);
        return MapFleetItem(row, reference, template);
    }

    public async Task<PipelineFleetUpdatePreviewDto> PreviewUpdateAsync(
        int pipelineId, int targetVersion, CancellationToken ct = default)
    {
        var (row, reference, template) = await GetTemplateContextAsync(pipelineId, ct).ConfigureAwait(false);
        var currentVersion = reference.Version ?? template.LatestVersion;
        // No run supplies the parameters here: a required one without a default (a candidate version) would
        // refuse the whole preview, so both sides keep their placeholders and the diff shows the definitions.
        var currentYaml = RewriteReference(row.YamlDefinition, reference.Name, currentVersion);
        var targetYaml = RewriteReference(row.YamlDefinition, reference.Name, targetVersion);
        var currentResolution = await templateResolver.ResolveAsync(
            currentYaml, row.OrganizationId, null, ct).ConfigureAwait(false);
        var targetResolution = await templateResolver.ResolveAsync(
            targetYaml, row.OrganizationId, null, ct).ConfigureAwait(false);
        var currentBaseResolution = await templateResolver.ResolveAsync(
            BuildTemplateResolutionYaml(template.Name, currentVersion), row.OrganizationId, null, ct)
            .ConfigureAwait(false);
        var targetBaseResolution = await templateResolver.ResolveAsync(
            BuildTemplateResolutionYaml(template.Name, targetVersion), row.OrganizationId, null, ct)
            .ConfigureAwait(false);
        var orphans = FindOrphanOverrides(
            row.YamlDefinition,
            currentBaseResolution.Yaml,
            targetBaseResolution.Yaml);
        return new PipelineFleetUpdatePreviewDto
        {
            PipelineId = pipelineId,
            TemplateName = template.Name,
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            CurrentResolvedYaml = currentResolution.Yaml,
            TargetResolvedYaml = targetResolution.Yaml,
            OrphanOverrides = orphans,
            SourceYamlHash = ComputeYamlHash(row.YamlDefinition)
        };
    }

    public async Task<PipelineDto> UpdateAsync(
        int pipelineId, PipelineFleetUpdateRequest request, CancellationToken ct = default)
    {
        var preview = await PreviewUpdateAsync(pipelineId, request.TargetVersion, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.ExpectedSourceYamlHash)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(preview.SourceYamlHash),
                Encoding.ASCII.GetBytes(request.ExpectedSourceYamlHash.ToUpperInvariant())))
            throw new ConflictException(
                "The pipeline changed after the preview. Reload the diff before applying this template version.");
        if (preview.RequiresOrphanAcknowledgement && !request.AcknowledgeOrphanOverrides)
            throw new BadRequestException(
                "The template update contains orphan overrides that must be explicitly acknowledged.");

        var row = await GetRowAsync(pipelineId, ct).ConfigureAwait(false);
        var yaml = RewriteReference(row.YamlDefinition, preview.TemplateName, request.TargetVersion);
        var updated = await RewritePipelineAsync(row, yaml, ct).ConfigureAwait(false);
        await audit.LogAsync(
            "TemplateVersionAdopted",
            "Pipeline",
            pipelineId,
            $"{preview.TemplateName}: v{preview.CurrentVersion} -> v{preview.TargetVersion}; "
                + $"orphanOverridesAcknowledged={request.AcknowledgeOrphanOverrides}",
            ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<PipelineTemplateDto> ExtractAsync(
        int pipelineId, ExtractPipelineTemplateRequest request, CancellationToken ct = default)
    {
        var row = await GetRowAsync(pipelineId, ct).ConfigureAwait(false);
        var createRequest = new CreatePipelineTemplateRequest
        {
            Name = request.TemplateName,
            Description = request.Description,
            Category = request.Category,
            OrganizationId = row.OrganizationId,
            YamlContent = string.IsNullOrWhiteSpace(request.TemplateYamlContent)
                ? row.YamlDefinition
                : request.TemplateYamlContent,
            ChangelogEntry = $"Extracted from pipeline '{row.PipelineName}'"
        };
        if (ParseReference(row.YamlDefinition) is { } existingReference)
        {
            if (existingReference.Version == 1
                && string.Equals(
                    existingReference.Name, request.TemplateName, StringComparison.OrdinalIgnoreCase))
            {
                var existingTemplate = await pipelineRepository.FindTemplateByNameAsync(
                    request.TemplateName, row.OrganizationId, ct).ConfigureAwait(false);
                if (existingTemplate is not null)
                {
                    var firstVersion = await GetVersionAsync(existingTemplate, 1, ct).ConfigureAwait(false);
                    if ((string.IsNullOrWhiteSpace(request.TemplateYamlContent)
                            || string.Equals(
                                firstVersion.YamlContent,
                                request.TemplateYamlContent,
                                StringComparison.Ordinal))
                        && string.Equals(
                            firstVersion.ChangelogEntry,
                            createRequest.ChangelogEntry,
                            StringComparison.Ordinal))
                    {
                        return await templateService.GetTemplateAsync(existingTemplate.Id, ct)
                            .ConfigureAwait(false)
                            ?? throw new NotFoundException(
                                $"Pipeline template {existingTemplate.Id} was not found.");
                    }
                }
            }
            throw new BadRequestException("Only an off-catalog pipeline can be extracted as a new template.");
        }

        if (!request.RewritePipeline)
            return await templateService.CreateTemplateAsync(createRequest, ct).ConfigureAwait(false);

        var rewrittenYaml = string.IsNullOrWhiteSpace(request.RewrittenPipelineYaml)
            ? $"name: {QuoteYaml(row.PipelineName)}\nextends: {QuoteYaml(request.TemplateName + "@1")}\nstages: []\n"
            : request.RewrittenPipelineYaml;
        return await PublishWithGitCompensationAsync(
            row,
            rewrittenYaml,
            () => templateService.CreateTemplateAsync(createRequest, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<PipelineTemplateDto> PromoteAsync(
        int pipelineId, PromotePipelineTemplateRequest request, CancellationToken ct = default)
    {
        var (row, reference, template) = await GetTemplateContextAsync(pipelineId, ct).ConfigureAwait(false);

        var updateRequest = new UpdatePipelineTemplateRequest
        {
            Name = template.Name,
            Description = template.Description,
            Category = template.Category,
            YamlContent = request.YamlContent,
            ChangelogEntry = request.ChangelogEntry
        };

        if (!request.RebaseSourcePipeline)
            return await templateService.UpdateTemplateAsync(template.Id, updateRequest, ct).ConfigureAwait(false)
                ?? throw new NotFoundException($"Pipeline template {template.Id} was not found.");

        if (reference.Version == template.LatestVersion)
        {
            var latest = await GetVersionAsync(template, template.LatestVersion, ct).ConfigureAwait(false);
            if (string.Equals(latest.YamlContent, request.YamlContent, StringComparison.Ordinal)
                && string.Equals(
                    latest.ChangelogEntry,
                    request.ChangelogEntry,
                    StringComparison.Ordinal))
            {
                return await templateService.GetTemplateAsync(template.Id, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException($"Pipeline template {template.Id} was not found.");
            }
        }

        var expectedVersion = template.LatestVersion + 1;
        var rewrittenYaml =
            $"name: {QuoteYaml(row.PipelineName)}\nextends: {QuoteYaml(template.Name + "@" + expectedVersion)}\nstages: []\n";
        return await PublishWithGitCompensationAsync(
            row,
            rewrittenYaml,
            async () =>
            {
                var published = await templateService.UpdateTemplateAsync(template.Id, updateRequest, ct)
                    .ConfigureAwait(false)
                    ?? throw new NotFoundException($"Pipeline template {template.Id} was not found.");
                if (published.Version != expectedVersion)
                    throw new ConflictException(
                        "The template version changed while the source pipeline was being rebased.");
                return published;
            },
            ct).ConfigureAwait(false);
    }

    public async Task<PipelinePromotePreviewDto> PreviewPromotionAsync(
        int pipelineId, CancellationToken ct = default)
    {
        var (row, reference, template) = await GetTemplateContextAsync(pipelineId, ct).ConfigureAwait(false);
        // Without parameters, like the update preview: a required one without a default must not refuse it.
        var effective = await templateResolver.ResolveAsync(
            row.YamlDefinition, row.OrganizationId, null, ct)
            .ConfigureAwait(false);
        var latest = await GetVersionAsync(template, template.LatestVersion, ct).ConfigureAwait(false);
        return new PipelinePromotePreviewDto
        {
            PipelineId = pipelineId,
            TemplateId = template.Id,
            TemplateName = template.Name,
            LatestVersion = template.LatestVersion,
            LatestTemplateYaml = latest.YamlContent,
            EffectivePipelineYaml = effective.Yaml
        };
    }

    private async Task<PipelineDto> RewritePipelineAsync(
        PipelineFleetRow row, string yaml, CancellationToken ct)
    {
        return await pipelineService.UpdatePipelineAsync(row.PipelineId, new UpdatePipelineRequest
        {
            Name = row.PipelineName,
            Description = row.Description,
            YamlDefinition = yaml,
            ProjectId = row.OwnerProjectId,
            EnvironmentId = row.EnvironmentId,
            ProjectServerId = row.ProjectServerId,
            SourceBranch = row.SourceBranch,
            AiRunnerProfileId = row.AiRunnerProfileId
        }, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Pipeline {row.PipelineId} was not found.");
    }

    private async Task<T> PublishWithGitCompensationAsync<T>(
        PipelineFleetRow row,
        string rewrittenYaml,
        Func<Task<T>> publish,
        CancellationToken ct)
    {
        var gitWasRewritten = false;
        try
        {
            async Task<T> WorkAsync()
            {
                await RewritePipelineAsync(row, rewrittenYaml, ct).ConfigureAwait(false);
                gitWasRewritten = true;
                return await publish().ConfigureAwait(false);
            }

            return transaction is { IsRelational: true }
                ? await transaction.ExecuteInTransactionAsync(WorkAsync, ct).ConfigureAwait(false)
                : await WorkAsync().ConfigureAwait(false);
        }
        catch
        {
            if (gitWasRewritten)
            {
                await RewritePipelineAsync(row, row.YamlDefinition, CancellationToken.None)
                    .ConfigureAwait(false);
                await audit.LogAsync(
                    "TemplatePublicationCompensated",
                    "Pipeline",
                    row.PipelineId,
                    "The source pipeline was restored after template publication failed.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<PipelineFleetRow> GetRowAsync(int pipelineId, CancellationToken ct) =>
        await fleetRepository.GetAsync(pipelineId, ct).ConfigureAwait(false)
        ?? throw new NotFoundException($"Pipeline {pipelineId} was not found.");

    private async Task<PipelineFleetTemplateContext> GetTemplateContextAsync(
        int pipelineId, CancellationToken ct)
    {
        var row = await GetRowAsync(pipelineId, ct).ConfigureAwait(false);
        var reference = ParseReference(row.YamlDefinition)
            ?? throw new BadRequestException("This pipeline is outside the template catalog.");
        var template = await FindTemplateAsync(reference.Name, row.OrganizationId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException($"Pipeline template '{reference.Name}' was not found.");
        return new PipelineFleetTemplateContext(row, reference, template);
    }

    /// <summary>The template resolver's rule (R-78): the organization's own template, otherwise the
    /// delivery template the platform shares with every organization.</summary>
    private async Task<Data.Entities.PipelineTemplate?> FindTemplateAsync(
        string name, int organizationId, CancellationToken ct) =>
        await pipelineRepository.FindTemplateByNameAsync(name, organizationId, ct).ConfigureAwait(false)
        ?? await pipelineRepository.FindSharedTemplateByNameAsync(name, ct).ConfigureAwait(false);

    private static PipelineFleetItemDto MapFleetItem(
        PipelineFleetRow row, TemplateReference? reference, Data.Entities.PipelineTemplate? template)
    {
        var pinnedVersion = reference?.Version;
        var freshness = reference is null
            ? PipelineFleetFreshness.OffCatalog
            : template is not null && (pinnedVersion ?? template.LatestVersion) == template.LatestVersion
                ? PipelineFleetFreshness.Current
                : PipelineFleetFreshness.Outdated;
        return new PipelineFleetItemDto
        {
            PipelineId = row.PipelineId,
            PipelineName = row.PipelineName,
            ProjectId = row.ProjectId,
            OwnerName = row.OwnerName,
            OwnerType = row.OwnerType,
            OrganizationId = row.OrganizationId,
            TemplateId = template?.Id,
            TemplateName = reference?.Name,
            PinnedVersion = pinnedVersion,
            LatestVersion = template?.LatestVersion,
            UsesLegacyReference = reference is { Version: null },
            Freshness = freshness
        };
    }

    private static TemplateReference? ParseReference(string yaml)
    {
        var match = ExtendsLinePattern.Match(yaml);
        if (!match.Success) return null;
        var value = match.Groups["reference"].Value.Trim().Trim('\'', '"');
        var separator = value.LastIndexOf('@');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var version) && version > 0)
            return new TemplateReference(value[..separator], version);
        return new TemplateReference(value, null);
    }

    private static string RewriteReference(string yaml, string name, int version)
    {
        if (!ExtendsLinePattern.IsMatch(yaml))
            throw new BadRequestException("The pipeline does not contain an extends reference.");
        return ExtendsLinePattern.Replace(yaml,
            match => match.Groups["prefix"].Value + QuoteYaml($"{name}@{version}") + match.Groups["suffix"].Value,
            1);
    }

    private static List<string> FindOrphanOverrides(string childYaml, string oldBaseYaml, string newBaseYaml)
    {
        var child = ParseDefinition(childYaml);
        var oldBase = ParseDefinition(oldBaseYaml);
        var newBase = ParseDefinition(newBaseYaml);
        var orphans = new List<string>();
        foreach (var childStage in child.Stages)
        {
            var oldStage = Find(oldBase.Stages, childStage.Name, stage => stage.Name);
            var newStage = Find(newBase.Stages, childStage.Name, stage => stage.Name);
            if (oldStage is not null && newStage is null)
            {
                orphans.Add($"stage:{childStage.Name}");
                continue;
            }
            if (oldStage is null || newStage is null) continue;
            FindNestedOrphans(childStage.Steps, oldStage.Steps, newStage.Steps,
                $"stage:{childStage.Name}/step", step => step.Name, orphans);
            foreach (var childJob in childStage.Jobs)
            {
                var oldJob = Find(oldStage.Jobs, childJob.Name, job => job.Name);
                var newJob = Find(newStage.Jobs, childJob.Name, job => job.Name);
                if (oldJob is not null && newJob is null)
                {
                    orphans.Add($"stage:{childStage.Name}/job:{childJob.Name}");
                    continue;
                }
                if (oldJob is not null && newJob is not null)
                    FindNestedOrphans(childJob.Steps, oldJob.Steps, newJob.Steps,
                        $"stage:{childStage.Name}/job:{childJob.Name}/step", step => step.Name, orphans);
            }
        }
        return orphans;
    }

    private static void FindNestedOrphans<T>(
        IReadOnlyList<T> child, IReadOnlyList<T> oldBase, IReadOnlyList<T> newBase,
        string prefix, Func<T, string> name, List<string> orphans) where T : class
    {
        foreach (var item in child)
            if (Find(oldBase, name(item), name) is not null && Find(newBase, name(item), name) is null)
                orphans.Add($"{prefix}:{name(item)}");
    }

    private static T? Find<T>(IReadOnlyList<T> items, string name, Func<T, string> selector) where T : class =>
        items.FirstOrDefault(item => string.Equals(selector(item), name, StringComparison.OrdinalIgnoreCase));

    private static PipelineYamlDefinition ParseDefinition(string yaml) =>
        YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml)
        ?? throw new BadRequestException("The pipeline YAML is empty.");

    private async Task<Data.Entities.PipelineTemplateVersion> GetVersionAsync(
        Data.Entities.PipelineTemplate template, int version, CancellationToken ct) =>
        template.Versions.FirstOrDefault(item => item.Version == version)
        ?? await pipelineRepository.GetTemplateVersionAsync(template.Id, version, ct).ConfigureAwait(false)
        ?? throw new BadRequestException(
            $"Pipeline template version '{template.Name}@{version}' does not exist.");

    private static string QuoteYaml(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string ComputeYamlHash(string yaml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));

    private static string BuildTemplateResolutionYaml(string name, int version) =>
        $"name: template-resolution\nextends: {QuoteYaml(name + "@" + version)}\nstages: []\n";

    private sealed record TemplateReference(string Name, int? Version);
    private sealed record PipelineFleetTemplateContext(
        PipelineFleetRow Row,
        TemplateReference Reference,
        Data.Entities.PipelineTemplate Template);
}
