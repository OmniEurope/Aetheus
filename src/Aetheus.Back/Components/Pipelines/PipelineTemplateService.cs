// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

public sealed class PipelineTemplateService(
    IPipelineTemplateRepository repo,
    IAuditService audit,
    ILogger<PipelineTemplateService> logger,
    TimeProvider timeProvider,
    IOrganizationRepository organizationRepository,
    IPipelineTemplateResolver templateResolver,
    IEntityChangeNotifier notifier,
    IHttpContextAccessor? httpContextAccessor = null) : IPipelineTemplateService
{
    private IPipelineTemplateResolver Resolver => templateResolver;

    public async Task<List<PipelineTemplateSummaryDto>> GetTemplatesAsync(CancellationToken ct = default)
    {
        return await repo.GetTemplateSummariesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PipelineTemplateDto?> GetTemplateAsync(int id, CancellationToken ct = default)
    {
        var template = await repo.GetTemplateAsync(id, ct).ConfigureAwait(false);
        if (template is null) return null;
        var latest = await repo.GetTemplateVersionAsync(id, template.LatestVersion, ct).ConfigureAwait(false);
        return MapTemplateToDto(template, latest);
    }

    public async Task<PaginatedResult<PipelineTemplateVersionSummaryDto>> GetTemplateVersionsAsync(
        int id, PaginationRequest request, CancellationToken ct = default)
    {
        if (await repo.GetTemplateAsync(id, ct).ConfigureAwait(false) is null)
            throw new NotFoundException("Pipeline template not found");
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetTemplateVersionsPagedAsync(
            id, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<PipelineTemplateVersionSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PipelineTemplateVersionDto?> GetTemplateVersionAsync(
        int id, int version, CancellationToken ct = default)
    {
        var item = await repo.GetTemplateVersionAsync(id, version, ct).ConfigureAwait(false);
        return item is null ? null : MapTemplateVersionToDto(item);
    }

    public async Task<PipelineTemplateDto> CreateTemplateAsync(
        CreatePipelineTemplateRequest request, CancellationToken ct = default)
    {
        var organizationId = request.OrganizationId
            ?? await GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false);
        await Resolver.ResolveAsync(request.YamlContent, organizationId, null, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var template = new PipelineTemplate
        {
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            OrganizationId = organizationId,
            LatestVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 1,
                    YamlContent = request.YamlContent,
                    ChangelogEntry = NormalizeChangelog(request.ChangelogEntry, "Initial version"),
                    CreatedAt = now,
                    CreatedByUsername = CurrentUsername()
                }
            ]
        };
        await repo.AddTemplateAsync(template, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "PipelineTemplate", template.Id, template.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(
            ResourceType.PipelineTemplate,
            template.Id,
            EntityChangeOps.Created,
            ct,
            organizationId).ConfigureAwait(false);
        return MapTemplateToDto(template, template.Versions.Single());
    }

    public async Task<PipelineTemplateDto?> UpdateTemplateAsync(
        int id, UpdatePipelineTemplateRequest request, CancellationToken ct = default)
    {
        var template = await repo.FindTemplateAsync(id, ct).ConfigureAwait(false);
        if (template is null) return null;
        if (!string.Equals(template.Name, request.Name, StringComparison.Ordinal))
            throw new BadRequestException(
                "A published pipeline template cannot be renamed because pinned references use its name as identity.");
        var changelog = NormalizeChangelog(request.ChangelogEntry, null);
        await Resolver.ResolveAsync(request.YamlContent, template.OrganizationId, null, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        template.Description = request.Description;
        template.Category = request.Category;
        template.LatestVersion++;
        template.UpdatedAt = now;
        var newVersion = new PipelineTemplateVersion
        {
            Version = template.LatestVersion,
            YamlContent = request.YamlContent,
            ChangelogEntry = changelog,
            CreatedAt = now,
            CreatedByUsername = CurrentUsername()
        };
        template.Versions.Add(newVersion);

        try
        {
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException)
        {
            throw new ConflictException(
                "The pipeline template was published by another request. Reload it before publishing a new version.");
        }
        await audit.LogAsync("Updated", "PipelineTemplate", template.Id,
            $"{template.Name}@{template.LatestVersion}: {changelog}", ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(
            ResourceType.PipelineTemplate,
            template.Id,
            EntityChangeOps.Updated,
            ct,
            template.OrganizationId).ConfigureAwait(false);
        return MapTemplateToDto(template, newVersion);
    }

    public async Task<bool> DeleteTemplateAsync(int id, CancellationToken ct = default)
    {
        var template = await repo.FindTemplateAsync(id, ct).ConfigureAwait(false);
        if (template is null) return false;
        var pipelineDefinitions = await repo.GetPipelineYamlDefinitionsByOrganizationAsync(
            template.OrganizationId, ct).ConfigureAwait(false) ?? [];
        var templateDefinitions = await repo.GetTemplateVersionYamlDefinitionsByOrganizationAsync(
            template.OrganizationId, template.Id, ct).ConfigureAwait(false) ?? [];
        if (pipelineDefinitions.Concat(templateDefinitions)
            .Any(yaml => ReferencesTemplate(yaml, template.Name)))
            throw new ConflictException(
                $"Pipeline template '{template.Name}' cannot be deleted while pipelines or templates reference it.");
        var name = template.Name;
        var organizationId = template.OrganizationId;
        await repo.RemoveTemplateAsync(template, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "PipelineTemplate", id, name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(
            ResourceType.PipelineTemplate,
            id,
            EntityChangeOps.Deleted,
            ct,
            organizationId).ConfigureAwait(false);
        return true;
    }

    public async Task<PipelineTemplateDto?> ImportTemplateAsync(
        string yamlContent, CancellationToken ct = default, int? organizationId = null)
    {
        PipelineYamlDefinition? definition;
        try
        {
            definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yamlContent);
        }
        catch (Exception exception) when (exception is YamlDotNet.Core.YamlException
            or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(exception, "Failed to parse imported YAML template");
            return null;
        }
        if (definition is null) return null;

        return await CreateTemplateAsync(new CreatePipelineTemplateRequest
        {
            Name = string.IsNullOrWhiteSpace(definition.Name) ? "Imported Template" : definition.Name,
            Description = string.Empty,
            Category = "Imported",
            OrganizationId = organizationId,
            YamlContent = yamlContent,
            ChangelogEntry = "Imported from YAML file"
        }, ct).ConfigureAwait(false);
    }

    public async Task<string?> ResolveTemplateAsync(
        string yamlContent,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default,
        int? organizationId = null)
    {
        var resolvedOrganizationId = organizationId
            ?? await GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false);
        var resolution = await Resolver.ResolveAsync(
            yamlContent, resolvedOrganizationId, parameters ?? new Dictionary<string, string>(), ct)
            .ConfigureAwait(false);
        return resolution.Yaml;
    }

    private async Task<int> GetDefaultOrganizationIdAsync(CancellationToken ct)
    {
        return await organizationRepository.GetDefaultOrganizationIdAsync(ct).ConfigureAwait(false)
            ?? throw new BadRequestException("No organization available; create an organization first.");
    }

    private string CurrentUsername() =>
        httpContextAccessor?.HttpContext?.User?.Identity?.Name ?? "system";

    private static string NormalizeChangelog(string? value, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        if (fallback is not null) return fallback;
        throw new BadRequestException("A changelog entry is required when publishing a template version.");
    }

    private static bool ReferencesTemplate(string yaml, string templateName)
    {
        try
        {
            var definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml);
            if (string.IsNullOrWhiteSpace(definition?.Extends)) return false;
            var reference = definition.Extends;
            var separator = reference.LastIndexOf('@');
            var name = separator > 0 ? reference[..separator] : reference;
            return string.Equals(name.Trim(), templateName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is YamlDotNet.Core.YamlException
            or InvalidOperationException or ArgumentException)
        {
            throw new ConflictException(
                "A stored pipeline definition is invalid, so template references cannot be verified safely.");
        }
    }

    private static PipelineTemplateDto MapTemplateToDto(
        PipelineTemplate template, PipelineTemplateVersion? latest)
    {
        return new PipelineTemplateDto
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description,
            Category = template.Category,
            OrganizationId = template.OrganizationId,
            YamlContent = latest?.YamlContent ?? string.Empty,
            Version = template.LatestVersion,
            Changelog = latest is null
                ? string.Empty
                : $"v{latest.Version}: {latest.ChangelogEntry}"
        };
    }

    private static PipelineTemplateVersionDto MapTemplateVersionToDto(PipelineTemplateVersion version) => new()
    {
        Version = version.Version,
        YamlContent = version.YamlContent,
        ChangelogEntry = version.ChangelogEntry,
        CreatedAt = version.CreatedAt,
        CreatedByUsername = version.CreatedByUsername
    };
}
