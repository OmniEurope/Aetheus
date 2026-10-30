// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Pipeline copies use <see cref="PipelineCopyAdapter"/> and the normal creation path in
/// the target repository. Libraries and vaults copy names only, leaving values and remaining
/// readiness requirements for the new project.</summary>
public sealed class PipelineProjectCopyService(
    IPipelineRepository pipelines,
    IPipelineService pipelineService,
    IPipelineSetupReadinessService readiness,
    IVariableLibraryService libraries,
    IVaultService vaults,
    ILogger<PipelineProjectCopyService> logger) : IPipelineProjectCopyService
{
    /// <summary>Candidates read per name: the search is a substring match, the exact name is kept.</summary>
    private const int SearchPageSize = 50;
    private const int MaxValidationErrors = 3;

    public async Task<PipelineProjectCopyPlanDto> PlanAsync(
        PipelineProjectCopyRequest request,
        List<int>? readablePipelineIds,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct = default) =>
        (await BuildPlanAsync(request, readablePipelineIds, readableLibraryIds, readableVaultIds, ct).ConfigureAwait(false)).ToDto();

    public async Task<PipelineProjectCopyResultDto> CopyAsync(
        PipelineProjectCopyRequest request,
        int? organizationId,
        List<int>? readablePipelineIds,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct = default)
    {
        var plan = await BuildPlanAsync(request, readablePipelineIds, readableLibraryIds, readableVaultIds, ct).ConfigureAwait(false);
        var createdLibraries = new List<int>();
        var createdVaults = new List<int>();
        var createdPipelines = new List<int>();
        try
        {
            foreach (var library in plan.Libraries.Where(item => !item.Dto.AlreadyExists))
            {
                var created = await libraries.CreateLibraryAsync(new CreateVariableLibraryRequest
                {
                    Name = library.Dto.NewName,
                    Description = StructureDescription(library.Dto.SourceName),
                    ProjectId = request.TargetProjectId
                }, ct).ConfigureAwait(false);
                createdLibraries.Add(created.Id);
                if (library.Dto.Keys.Count > 0)
                    await libraries.ImportEntriesAsync(created.Id,
                        [.. library.Dto.Keys.Select(key => new CreateVariableEntryRequest { Key = key, Value = string.Empty })],
                        ct).ConfigureAwait(false);
                library.CreatedId = created.Id;
            }

            foreach (var vault in plan.Vaults.Where(item => !item.Dto.AlreadyExists))
            {
                var created = await vaults.CreateVaultAsync(new CreateVaultRequest
                {
                    Name = vault.Dto.NewName,
                    Description = StructureDescription(vault.Dto.SourceName),
                    ProjectId = request.TargetProjectId
                }, ct).ConfigureAwait(false);
                createdVaults.Add(created.Id);
                if (vault.Dto.Keys.Count > 0)
                    await vaults.ImportSecretsAsync(created.Id,
                        [.. vault.Dto.Keys.Select(key => new CreateVaultSecretRequest { Key = key, Value = string.Empty })],
                        ct).ConfigureAwait(false);
                vault.CreatedId = created.Id;
            }

            foreach (var item in plan.Pipelines.Where(item => item.Status != PipelineCopyStatus.NotCopied))
            {
                var created = await pipelineService.CreatePipelineAsync(new CreatePipelineRequest
                {
                    Name = item.NewName,
                    Description = $"Copied from pipeline '{item.SourceName}' of another project by the setup wizard.",
                    YamlDefinition = item.Yaml,
                    ProjectId = request.TargetProjectId,
                    SourceBranch = string.IsNullOrWhiteSpace(request.TargetBranch) ? null : request.TargetBranch.Trim()
                }, ct).ConfigureAwait(false);
                createdPipelines.Add(created.Id);
                item.CreatedId = created.Id;
            }
        }
        catch
        {
            // Mandatory compensation: a half-made copy (libraries without the pipelines that use them,
            // or the first pipelines of a chain without the rest) is worse than none, and the wizard
            // could not tell which part exists. CancellationToken.None: the removal must run even when
            // ct is what failed.
            foreach (var id in createdPipelines.AsEnumerable().Reverse())
                await pipelineService.DeletePipelineAsync(id, CancellationToken.None).ConfigureAwait(false);
            foreach (var id in createdVaults)
                await vaults.DeleteVaultAsync(id, CancellationToken.None).ConfigureAwait(false);
            foreach (var id in createdLibraries)
                await libraries.DeleteLibraryAsync(id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        logger.LogInformation(
            "Copied {PipelineCount} pipeline(s), {LibraryCount} library structure(s) and {VaultCount} vault structure(s) from project {SourceProjectId} to project {TargetProjectId}.",
            createdPipelines.Count, createdLibraries.Count, createdVaults.Count, request.SourceProjectId, request.TargetProjectId);

        var verdict = createdPipelines.Count == 0
            ? new PipelineSetupReadinessDto()
            : await readiness.CheckProjectAsync(request.TargetProjectId, pipelineId: null, organizationId, ct).ConfigureAwait(false);
        return new PipelineProjectCopyResultDto { Plan = plan.ToDto(), Readiness = verdict };
    }

    // The libraries and vaults each source pipeline reads, its template included: the definition
    // a run of the SOURCE pipeline would read.
    private async Task<(List<string> Libraries, List<string> Vaults)> CollectReferencedResourcesAsync(
        IEnumerable<(int Id, string Name, string Yaml)> sources,
        int sourceProjectId,
        string? sourceSlug,
        CancellationToken ct)
    {
        var libraryNames = new List<string>();
        var vaultNames = new List<string>();
        // R3-67: a template's `${{ project.slug }}.share` is, for the source pipeline, the source
        // project's library.
        foreach (var source in sources)
        {
            var validation = await pipelineService.ValidateYamlStrictAsync(
                source.Yaml, sourceProjectId, null, null, ct: ct).ConfigureAwait(false);
            var definition = validation.Definition ?? YamlParsingHelper.ParseAndValidate(source.Yaml, logger);
            if (PipelineRequirementsProvisioner.ReferencedResources(
                    definition is null ? null : PipelineProjectResourceNames.Expand(definition, sourceSlug)) is { } referenced)
            {
                libraryNames.AddRange(referenced.Libraries);
                vaultNames.AddRange(referenced.Vaults);
            }
        }
        return (libraryNames, vaultNames);
    }

    private async Task<CopyPlan> BuildPlanAsync(
        PipelineProjectCopyRequest request,
        List<int>? readablePipelineIds,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceProjectId == request.TargetProjectId)
            throw new BadRequestException("The source project and the new project must be different.");

        var selected = request.PipelineIds.ToHashSet();
        var sources = (await pipelines.GetPipelineDefinitionsForProjectAsync(request.SourceProjectId, ct).ConfigureAwait(false))
            .Where(source => readablePipelineIds is null || readablePipelineIds.Contains(source.Id))
            .Where(source => selected.Count == 0 || selected.Contains(source.Id))
            .ToList();
        var taken = (await pipelines.GetPipelineDefinitionsForProjectAsync(request.TargetProjectId, ct).ConfigureAwait(false))
            .Select(existing => existing.Name)
            .ToHashSet(StringComparer.Ordinal);

        // R-86: a library or vault named after the source project's slug (`w7.share`) is copied under the
        // target project's slug, the name the copied pipelines read (`${{ project.slug }}.share`).
        var slugs = new ProjectSlugPair(
            await PipelineProjectResourceNames.FindSlugAsync(pipelines, request.SourceProjectId, ct).ConfigureAwait(false),
            await PipelineProjectResourceNames.FindSlugAsync(pipelines, request.TargetProjectId, ct).ConfigureAwait(false));
        var (libraryNames, vaultNames) = await CollectReferencedResourcesAsync(
            sources, request.SourceProjectId, slugs.Source, ct).ConfigureAwait(false);

        var plan = new CopyPlan();
        foreach (var name in libraryNames.Distinct(StringComparer.OrdinalIgnoreCase))
            if (await PlanLibraryAsync(name, request, slugs, readableLibraryIds, ct).ConfigureAwait(false) is { } library)
                plan.Libraries.Add(new PlannedResource(library));
        foreach (var name in vaultNames.Distinct(StringComparer.OrdinalIgnoreCase))
            if (await PlanVaultAsync(name, request, slugs, readableVaultIds, ct).ConfigureAwait(false) is { } vault)
                plan.Vaults.Add(new PlannedResource(vault));

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
            renames[source.Name] = PipelineCopyAdapter.NewName(source.Name, request.SourcePrefix, request.Prefix);
        foreach (var resource in plan.Libraries.Concat(plan.Vaults))
            renames.TryAdd(resource.Dto.SourceName, resource.Dto.NewName);
        var copiedNames = sources.Select(source => renames[source.Name]).ToHashSet(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var newName = renames[source.Name];
            var (adapted, issues) = PipelineCopyAdapter.Adapt(source.Yaml, renames, request.SourcePrefix, request.Prefix, copiedNames);
            // R-76: the copy reads the target project's branch, not the one its source project worked on.
            var yaml = PipelineCopySourceBranch.Rewrite(adapted, request.TargetBranch);
            var item = new PlannedPipeline(source.Id, source.Name, newName, yaml) { Issues = issues };
            if (!taken.Add(newName))
            {
                item.Status = PipelineCopyStatus.NotCopied;
                item.Issues = [new PipelineCopyIssueDto { Kind = PipelineCopyIssueKind.NameTaken, Detail = newName }];
            }
            else if (PipelineCopyTriggers.IsActive(yaml))
            {
                // Fail closed: a trigger written in a shape the rewrite does not recognise would leave
                // the copy running on its own in the new project.
                item.Status = PipelineCopyStatus.NotCopied;
                item.Issues = [.. issues, new PipelineCopyIssueDto
                {
                    Kind = PipelineCopyIssueKind.InvalidDefinition,
                    Detail = "The webhook or schedule trigger could not be disabled safely; copy this pipeline by hand."
                }];
            }
            else
            {
                var validation = await pipelineService.ValidateYamlStrictAsync(
                    yaml, request.TargetProjectId, null, null, ct: ct).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    item.Status = PipelineCopyStatus.NotCopied;
                    item.Issues = [.. issues, new PipelineCopyIssueDto
                    {
                        Kind = PipelineCopyIssueKind.InvalidDefinition,
                        Detail = string.Join(" ", validation.Errors.Take(MaxValidationErrors))
                    }];
                }
                else
                {
                    item.Status = issues.TrueForAll(issue => PipelineCopyAdapter.IsInformational(issue.Kind))
                        ? PipelineCopyStatus.Ready
                        : PipelineCopyStatus.ToReview;
                }
            }
            plan.Pipelines.Add(item);
        }
        return plan;
    }

    /// <summary>The source project's own library of this name, if the caller can read it, with the
    /// names of its variables. A library the source project does not own is not copied: the target's
    /// readiness verdict will name it if the copies need it.</summary>
    private async Task<PipelineCopyResourceDto?> PlanLibraryAsync(
        string name, PipelineProjectCopyRequest request, ProjectSlugPair slugs, List<int>? readableIds, CancellationToken ct)
    {
        var source = (await libraries.GetLibrariesAsync(request.SourceProjectId,
                request: new PaginationRequest { Page = 1, PageSize = SearchPageSize, Search = name },
                accessibleIds: readableIds, ct: ct).ConfigureAwait(false)).Items
            .FirstOrDefault(item => item.ProjectId == request.SourceProjectId
                && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (source is null) return null;

        var newName = PipelineCopyAdapter.NewResourceName(
            source.Name, request.SourcePrefix, request.Prefix, slugs.Source, slugs.Target);
        var exists = (await libraries.GetLibrariesAsync(request.TargetProjectId,
                request: new PaginationRequest { Page = 1, PageSize = SearchPageSize, Search = newName },
                ct: ct).ConfigureAwait(false)).Items
            .Any(item => item.ProjectId == request.TargetProjectId
                && string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase));
        // Only the keys leave this method: the values the export carries are dropped here.
        var keys = (await libraries.ExportEntriesAsync(source.Id, ct).ConfigureAwait(false))
            .Select(entry => entry.Key).Distinct(StringComparer.Ordinal).ToList();
        return new PipelineCopyResourceDto { SourceName = source.Name, NewName = newName, Keys = keys, AlreadyExists = exists };
    }

    /// <summary>Same as <see cref="PlanLibraryAsync"/> for a vault: secret names only, the export
    /// used never carries a value.</summary>
    private async Task<PipelineCopyResourceDto?> PlanVaultAsync(
        string name, PipelineProjectCopyRequest request, ProjectSlugPair slugs, List<int>? readableIds, CancellationToken ct)
    {
        var source = (await vaults.GetVaultsAsync(request.SourceProjectId,
                request: new PaginationRequest { Page = 1, PageSize = SearchPageSize, Search = name },
                accessibleIds: readableIds, ct: ct).ConfigureAwait(false)).Items
            .FirstOrDefault(item => item.ProjectId == request.SourceProjectId
                && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (source is null) return null;

        var newName = PipelineCopyAdapter.NewResourceName(
            source.Name, request.SourcePrefix, request.Prefix, slugs.Source, slugs.Target);
        var exists = (await vaults.GetVaultsAsync(request.TargetProjectId,
                request: new PaginationRequest { Page = 1, PageSize = SearchPageSize, Search = newName },
                ct: ct).ConfigureAwait(false)).Items
            .Any(item => item.ProjectId == request.TargetProjectId
                && string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase));
        var keys = (await vaults.ExportSecretKeysAsync(source.Id, ct).ConfigureAwait(false))
            .Distinct(StringComparer.Ordinal).ToList();
        return new PipelineCopyResourceDto { SourceName = source.Name, NewName = newName, Keys = keys, AlreadyExists = exists };
    }

    // English: backend-generated strings follow the product default language (F-036).
    private static string StructureDescription(string sourceName) =>
        $"Copied from '{sourceName}' of another project by the setup wizard: names only, the values are empty. Fill them in.";

    /// <summary>The slugs of the two projects, as <see cref="PipelineProjectResourceNames.FindSlugAsync"/> finds them.</summary>
    private readonly record struct ProjectSlugPair(string? Source, string? Target);

    private sealed class CopyPlan
    {
        public List<PlannedPipeline> Pipelines { get; } = [];
        public List<PlannedResource> Libraries { get; } = [];
        public List<PlannedResource> Vaults { get; } = [];

        public PipelineProjectCopyPlanDto ToDto() => new()
        {
            Pipelines = [.. Pipelines.Select(item => new PipelineCopyItemDto
            {
                SourcePipelineId = item.SourceId,
                SourceName = item.SourceName,
                NewName = item.NewName,
                Status = item.Status,
                Issues = item.Issues,
                CreatedPipelineId = item.CreatedId
            })],
            Libraries = [.. Libraries.Select(item => item.Dto with { CreatedId = item.CreatedId })],
            Vaults = [.. Vaults.Select(item => item.Dto with { CreatedId = item.CreatedId })]
        };
    }

    private sealed class PlannedPipeline(int sourceId, string sourceName, string newName, string yaml)
    {
        public int SourceId { get; } = sourceId;
        public string SourceName { get; } = sourceName;
        public string NewName { get; } = newName;
        public string Yaml { get; } = yaml;
        public PipelineCopyStatus Status { get; set; }
        public List<PipelineCopyIssueDto> Issues { get; set; } = [];
        public int? CreatedId { get; set; }
    }

    private sealed class PlannedResource(PipelineCopyResourceDto dto)
    {
        public PipelineCopyResourceDto Dto { get; } = dto;
        public int? CreatedId { get; set; }
    }
}
