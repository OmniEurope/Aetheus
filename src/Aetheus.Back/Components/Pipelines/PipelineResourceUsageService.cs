// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineResourceUsageService
{
    /// <summary>For each readable library and vault, who reads it. <paramref name="readablePipelineIds"/>
    /// null means every pipeline is readable; a pipeline outside the list is reported anonymously.</summary>
    Task<ResourceUsageDto> GetAsync(
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        List<int>? readablePipelineIds,
        CancellationToken ct = default);
}

/// <summary>
/// Recette R-65, R-66: inverts the pipelines' definitions into "used by". A pipeline reads a library or a
/// vault when its definition, as a run would read it (<c>extends:</c> expanded, the project slug
/// expression replaced), names it in <c>variable_libraries:</c>, <c>vaults:</c> or <c>requires:</c>;
/// the name resolves to the resource the pipeline's project owns (directly or through an environment or
/// a server) or to an ownerless one, as <c>FindByNamesWithCrossAccessAsync</c> does for a run. The vault
/// a monitored application keeps its web analytics keys in is read by that application, a use no
/// pipeline shows. A mention in a definition does not prove a run reads the value (a comment, a
/// <c>:-</c> default): the answer is "referenced by", which is what stops a deletion from being blind.
/// </summary>
public sealed class PipelineResourceUsageService(
    IPipelineResourceUsageRepository repository,
    IPipelineTemplateRepository templateRepository,
    ILogger<PipelineResourceUsageService> logger) : IPipelineResourceUsageService
{
    private sealed record Reference(string Name, int? ProjectId, ResourceUsageUserDto User);

    public async Task<ResourceUsageDto> GetAsync(
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        List<int>? readablePipelineIds,
        CancellationToken ct = default)
    {
        var libraries = await repository.GetLibrariesAsync(readableLibraryIds, ct).ConfigureAwait(false);
        var vaults = await repository.GetVaultsAsync(readableVaultIds, ct).ConfigureAwait(false);
        // One resolver over a call-scoped cache: pipelines sharing a template read its rows once.
        var templates = new PipelineTemplateResolver(new MemoizingPipelineTemplateRepository(templateRepository));
        var libraryReferences = new List<Reference>();
        var vaultReferences = new List<Reference>();

        foreach (var pipeline in await repository.GetPipelinesAsync(ct).ConfigureAwait(false))
        {
            var referenced = PipelineRequirementsProvisioner.ReferencedResources(
                await ResolveAsync(templates, pipeline, ct).ConfigureAwait(false));
            if (referenced is null) continue;

            var readable = readablePipelineIds is null || readablePipelineIds.Contains(pipeline.Id);
            var user = readable
                ? new ResourceUsageUserDto { Kind = ResourceUsageKind.Pipeline, Id = pipeline.Id, Name = pipeline.Name, ProjectId = pipeline.ProjectId }
                : new ResourceUsageUserDto { Kind = ResourceUsageKind.Pipeline };
            libraryReferences.AddRange(referenced.Libraries.Select(name => new Reference(name, pipeline.ProjectId, user)));
            vaultReferences.AddRange(referenced.Vaults.Select(name => new Reference(name, pipeline.ProjectId, user)));
        }

        foreach (var app in await repository.GetAnalyticsAppsAsync(ct).ConfigureAwait(false))
            vaultReferences.Add(new Reference(app.VaultName, app.ProjectId, new ResourceUsageUserDto
            {
                Kind = ResourceUsageKind.MonitoredApp,
                Id = app.Id,
                Name = app.Name,
                ProjectId = app.ProjectId
            }));

        return new ResourceUsageDto
        {
            Libraries = Invert(libraries, libraryReferences),
            Vaults = Invert(vaults, vaultReferences)
        };
    }

    /// <summary>The resources that something reads, each with its readers once, in a stable order.</summary>
    private static Dictionary<int, List<ResourceUsageUserDto>> Invert(
        List<UsageResourceRow> resources, List<Reference> references)
    {
        var usage = new Dictionary<int, List<ResourceUsageUserDto>>();
        foreach (var resource in resources)
        {
            var users = references
                .Where(reference => string.Equals(reference.Name, resource.Name, StringComparison.Ordinal)
                    && (resource.OwnerProjectId is null || resource.OwnerProjectId == reference.ProjectId))
                .Select(reference => reference.User)
                .Distinct()
                .OrderBy(user => user.Kind)
                .ThenBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (users.Count > 0) usage[resource.Id] = users;
        }
        return usage;
    }

    /// <summary>The definition a run would read; the pipeline's own YAML when its template cannot be
    /// resolved, so one broken pipeline never hides the others' usage.</summary>
    private async Task<PipelineYamlDefinition?> ResolveAsync(
        IPipelineTemplateResolver templates, UsagePipelineRow pipeline, CancellationToken ct)
    {
        PipelineYamlDefinition? definition = null;
        if (pipeline.OrganizationId is { } organization)
        {
            try
            {
                definition = (await templates.ResolveAsync(pipeline.Yaml, organization, parameters: null, ct).ConfigureAwait(false)).Definition;
            }
            catch (Exception exception) when (exception is BadRequestException or NotFoundException
                                                  or YamlDotNet.Core.YamlException or InvalidOperationException)
            {
                logger.LogDebug(exception, "Resource usage could not resolve the template of pipeline {PipelineId}; reading its own YAML.", pipeline.Id);
            }
        }

        definition ??= YamlParsingHelper.ParseAndValidate(pipeline.Yaml, logger);
        if (definition is null || pipeline.ProjectId is not { } projectId || pipeline.ProjectName is null) return definition;
        return PipelineProjectResourceNames.Expand(definition, ProjectResourceNames.Slug(pipeline.ProjectName, projectId));
    }
}
