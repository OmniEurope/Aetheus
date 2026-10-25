// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Provisioning creates required library and vault keys from accessible resources but
/// fills only the keys the generic templates read (R2-21) and leaves every other value empty, so another
/// project's hosts or secrets are never copied.</summary>
public sealed class PipelineRequirementsProvisioner(
    IPipelineSetupReadinessService readiness,
    IPipelineRepository pipelines,
    IPipelineRequirementsChecker requirements,
    IPipelineTemplateResolver templates,
    IVariableLibraryService libraries,
    IVaultService vaults,
    ILogger<PipelineRequirementsProvisioner> logger) : IPipelineRequirementsProvisioner
{
    /// <summary>Candidates read per name. A name shared by more than this many readable resources is
    /// already an unusual installation; the most recently updated of the first page is used.</summary>
    private const int SourceSearchPageSize = 50;

    public async Task<PipelineRequirementsProvisionResultDto> ProvisionAsync(
        int projectId,
        IReadOnlyList<string> templateNames,
        int? organizationId,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templateNames);

        // The readiness check is the single statement of what is missing: provisioning exactly its
        // findings means the button can never create something the launch did not need, nor skip
        // something it did.
        var verdict = await readiness.CheckAsync(projectId, templateNames, organizationId, ct).ConfigureAwait(false);
        var result = await CreateAllAsync(
            projectId,
            ItemsOf(verdict, PipelineSetupReadinessKind.MissingRequiredLibraries),
            ItemsOf(verdict, PipelineSetupReadinessKind.MissingRequiredVaults),
            readableLibraryIds, readableVaultIds, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Provisioned {LibraryCount} library(ies) and {VaultCount} vault(s) for project {ProjectId} from the requirements of {Templates}.",
            result.Libraries.Count, result.Vaults.Count, projectId, string.Join(", ", templateNames));
        return result;
    }

    public async Task<PipelineRequirementsProvisionResultDto> ProvisionUnmetAsync(
        int projectId,
        int? organizationId,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct = default)
    {
        // The same list the overview warning shows, so the button creates exactly what it names.
        var unmet = await GetUnmetAsync(projectId, organizationId, ct).ConfigureAwait(false);
        var result = await CreateAllAsync(
            projectId,
            [.. unmet.Where(item => item.Kind == PipelineRequirementsChecker.LibraryKind).Select(item => item.Name)],
            [.. unmet.Where(item => item.Kind == PipelineRequirementsChecker.VaultKind).Select(item => item.Name)],
            readableLibraryIds, readableVaultIds, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Provisioned {LibraryCount} library(ies) and {VaultCount} vault(s) for project {ProjectId} from its pipelines' unmet requirements.",
            result.Libraries.Count, result.Vaults.Count, projectId);
        return result;
    }

    private async Task<PipelineRequirementsProvisionResultDto> CreateAllAsync(
        int projectId,
        List<string> missingLibraries,
        List<string> missingVaults,
        List<int>? readableLibraryIds,
        List<int>? readableVaultIds,
        CancellationToken ct)
    {
        var createdLibraries = new List<ProvisionedRequirementDto>(missingLibraries.Count);
        foreach (var name in missingLibraries)
            createdLibraries.Add(await CreateLibraryAsync(projectId, name, readableLibraryIds, ct).ConfigureAwait(false));

        var createdVaults = new List<ProvisionedRequirementDto>(missingVaults.Count);
        foreach (var name in missingVaults)
            createdVaults.Add(await CreateVaultAsync(projectId, name, readableVaultIds, ct).ConfigureAwait(false));

        return new PipelineRequirementsProvisionResultDto { Libraries = createdLibraries, Vaults = createdVaults };
    }


    public async Task<List<UnmetRequirementDto>> GetUnmetAsync(
        int projectId, int? organizationId, CancellationToken ct = default)
    {
        // The same checker the launch preflight uses, so the overview warns about exactly what a
        // launch would refuse, and stops warning the moment the resource exists.
        // Keyed case-insensitively (library names resolve that way) while keeping the name as written.
        var byResource = new Dictionary<string, (string Kind, string Name, List<string> Waiting)>(StringComparer.OrdinalIgnoreCase);
        int? owningOrganizationId = null;
        var referencedByPipeline = new List<(string Pipeline, PipelineRequiresDefinition Referenced)>();
        // R3-67: `${{ project.slug }}.share` is this project's library, as a run reads it.
        var slug = await PipelineProjectResourceNames.FindSlugAsync(pipelines, projectId, ct).ConfigureAwait(false);
        foreach (var (pipelineId, name, yaml) in await pipelines.GetPipelineDefinitionsForProjectAsync(projectId, ct).ConfigureAwait(false))
        {
            owningOrganizationId ??= await pipelines.GetPipelineOrganizationIdAsync(pipelineId, ct).ConfigureAwait(false);
            var definition = await ResolveAsync(yaml, owningOrganizationId, ct).ConfigureAwait(false);
            if (definition is not null) definition = PipelineProjectResourceNames.Expand(definition, slug);
            if (ReferencedResources(definition) is { } referenced)
                referencedByPipeline.Add((name, referenced));
        }
        if (referencedByPipeline.Count == 0) return [];

        // Resolve the project's libraries and vaults once for all pipeline references.
        var everything = new PipelineRequiresDefinition
        {
            Libraries = [.. referencedByPipeline.SelectMany(item => item.Referenced.Libraries).Distinct(StringComparer.OrdinalIgnoreCase)],
            Vaults = [.. referencedByPipeline.SelectMany(item => item.Referenced.Vaults).Distinct(StringComparer.OrdinalIgnoreCase)]
        };
        foreach (var outcome in await requirements.CheckAsync(everything, projectId, organizationId, ct).ConfigureAwait(false))
        {
            if (outcome.Satisfied) continue;
            if (outcome.Kind is not (PipelineRequirementsChecker.LibraryKind or PipelineRequirementsChecker.VaultKind)) continue;

            var waiting = referencedByPipeline
                .Where(item => (outcome.Kind == PipelineRequirementsChecker.LibraryKind ? item.Referenced.Libraries : item.Referenced.Vaults)
                    .Contains(outcome.Name, StringComparer.OrdinalIgnoreCase))
                .Select(item => item.Pipeline)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            byResource[$"{outcome.Kind}:{outcome.Name}"] = (outcome.Kind, outcome.Name, waiting);
        }

        return [.. byResource.Values
            .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new UnmetRequirementDto
            {
                Kind = entry.Kind,
                Name = entry.Name,
                Pipelines = entry.Waiting
            })];
    }

    /// <summary>
    /// The definition a launch would run: a pipeline created by the wizard is only
    /// <c>extends: template@version</c>, and its libraries and vaults come from the template. Reading
    /// the stored YAML alone would never warn about them. A template that cannot be resolved (deleted,
    /// or another organization's) falls back to the pipeline's own YAML rather than hiding the rest.
    /// </summary>
    private async Task<PipelineYamlDefinition?> ResolveAsync(string yaml, int? organizationId, CancellationToken ct)
    {
        if (organizationId is { } organization)
        {
            try
            {
                return (await templates.ResolveAsync(yaml, organization, parameters: null, ct).ConfigureAwait(false)).Definition;
            }
            catch (BadRequestException ex)
            {
                logger.LogDebug(ex, "Pipeline template could not be resolved for the unmet-requirements check; reading the pipeline's own YAML.");
            }
        }

        return YamlParsingHelper.ParseAndValidate(yaml, logger);
    }

    /// <summary>
    /// Every library and vault the pipeline refers to: those it declares in <c>requires:</c> and those
    /// it reads through <c>variable_libraries:</c> and <c>vaults:</c>. A launch refuses a missing vault
    /// and warns for a missing library, so both belong in the warning. Null when there are none.
    /// </summary>
    internal static PipelineRequiresDefinition? ReferencedResources(PipelineYamlDefinition? definition)
    {
        if (definition is null) return null;
        var declared = definition.Requires ?? new PipelineRequiresDefinition();
        var libraries = declared.Libraries.Concat(definition.VariableLibraries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var vaults = declared.Vaults.Concat(definition.Vaults)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return libraries.Count == 0 && vaults.Count == 0
            ? null
            : new PipelineRequiresDefinition { Libraries = libraries, Vaults = vaults };
    }

    private static List<string> ItemsOf(PipelineSetupReadinessDto verdict, PipelineSetupReadinessKind kind) =>
        [.. verdict.Checks
            .Where(check => check.Kind == kind)
            .SelectMany(check => check.Items)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private async Task<ProvisionedRequirementDto> CreateLibraryAsync(
        int projectId, string name, List<int>? readableIds, CancellationToken ct)
    {
        var candidates = await libraries.GetLibrariesAsync(
            projectId: null,
            request: new PaginationRequest { Page = 1, PageSize = SourceSearchPageSize, Search = name },
            accessibleIds: readableIds,
            ct: ct).ConfigureAwait(false);
        var source = PickSource(candidates.Items, name, projectId);
        var sourceKeys = source is null
            ? []
            : (await libraries.ExportEntriesAsync(source.Id, ct).ConfigureAwait(false))
                .Select(entry => entry.Key)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        // R3-67: the name the templates give the library is `<slug>.share|qa|prod`; the catalog knows the
        // keys those templates read and how each one is filled (R2-21).
        var slug = await PipelineProjectResourceNames.FindSlugAsync(pipelines, projectId, ct).ConfigureAwait(false);
        var plan = KeyPlan.Build(PipelineRequirementKeys.ForLibrary(name, slug), sourceKeys);

        var created = await libraries.CreateLibraryAsync(new CreateVariableLibraryRequest
        {
            Name = name,
            Description = DescriptionFor(source, plan),
            ProjectId = projectId
        }, ct).ConfigureAwait(false);
        var filled = plan.FilledValueKeys;
        try
        {
            if (plan.Values.Count > 0)
            {
                await libraries.ImportEntriesAsync(
                    created.Id,
                    [.. plan.Values.Select(entry => new CreateVariableEntryRequest { Key = entry.Key, Value = entry.Value })],
                    ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Mandatory compensation: a library left without its keys satisfies the requirement,
            // so it would drop out of every later check and its keys would never be offered again.
            // CancellationToken.None: the removal must run even when ct is what failed the import.
            await libraries.DeleteLibraryAsync(created.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new ProvisionedRequirementDto
        {
            Id = created.Id,
            Name = name,
            Keys = plan.Keys,
            FilledKeys = filled,
            KeysCopiedFrom = SourceLabel(source)
        };
    }

    private async Task<ProvisionedRequirementDto> CreateVaultAsync(
        int projectId, string name, List<int>? readableIds, CancellationToken ct)
    {
        var candidates = await vaults.GetVaultsAsync(
            projectId: null,
            request: new PaginationRequest { Page = 1, PageSize = SourceSearchPageSize, Search = name },
            accessibleIds: readableIds,
            ct: ct).ConfigureAwait(false);
        var source = PickSource(candidates.Items, name, projectId);
        var sourceKeys = source is null
            ? []
            : (await vaults.ExportSecretKeysAsync(source.Id, ct).ConfigureAwait(false))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        var slug = await PipelineProjectResourceNames.FindSlugAsync(pipelines, projectId, ct).ConfigureAwait(false);
        var plan = KeyPlan.Build(PipelineRequirementKeys.ForVault(name, slug), sourceKeys);

        var created = await vaults.CreateVaultAsync(new CreateVaultRequest
        {
            Name = name,
            Description = DescriptionFor(source, plan),
            ProjectId = projectId
        }, ct).ConfigureAwait(false);
        if (plan.Values.Count > 0)
        {
            try
            {
                await vaults.ImportSecretsAsync(
                    created.Id,
                    [.. plan.Values.Select(entry => new CreateVaultSecretRequest { Key = entry.Key, Value = entry.Value })],
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Same compensation as the library: never a vault left without its secret names.
                await vaults.DeleteVaultAsync(created.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        return new ProvisionedRequirementDto
        {
            Id = created.Id,
            Name = name,
            Keys = plan.Keys,
            FilledKeys = plan.FilledValueKeys,
            KeysCopiedFrom = SourceLabel(source)
        };
    }

    /// <summary>The keys to write and their values: the catalog's keys with the value their rule gives,
    /// then the key names of a same-named resource the catalog does not know (always empty, so another
    /// project's values are never copied).</summary>
    private sealed record KeyPlan(List<KeyValuePair<string, string>> Values)
    {
        public static KeyPlan Build(IReadOnlyList<RequirementKey> catalog, IEnumerable<string> sourceKeys)
        {
            var values = catalog
                .Select(key => new KeyValuePair<string, string>(key.Key, key.Value()))
                .ToList();
            var known = catalog.Select(key => key.Key).ToHashSet(StringComparer.Ordinal);
            values.AddRange(sourceKeys
                .Where(key => !known.Contains(key))
                .Select(key => new KeyValuePair<string, string>(key, string.Empty)));
            return new KeyPlan(values);
        }

        public List<string> Keys => [.. Values.Select(entry => entry.Key)];

        public List<string> FilledValueKeys =>
            [.. Values.Where(entry => entry.Value.Length > 0).Select(entry => entry.Key)];
    }

    /// <summary>
    /// The resource to read key names from: exactly the same name (the search is a substring match),
    /// never one belonging to the project being provisioned, most recently updated first.
    /// </summary>
    private static T? PickSource<T>(IEnumerable<T> candidates, string name, int projectId)
        where T : OwnedResourceDto =>
        candidates
            .Where(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
                && candidate.ProjectId != projectId)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefault();

    private static string? SourceLabel(OwnedResourceDto? source) => source is null
        ? null
        : $"{source.ProjectName ?? "-"} / {source.Name}";

    private static string DescriptionFor(OwnedResourceDto? source, KeyPlan plan)
    {
        var keys = source is null
            ? "No readable resource of this name existed, so only the keys the generic templates read were created."
            : $"Keys from {SourceLabel(source)}.";
        var values = plan.FilledValueKeys.Count > 0
            ? "Generated secrets and names derived from the project are filled in; the other values are empty: fill them in."
            : "Values are empty: fill them in.";
        return $"Created from the pipeline requirements. {keys} {values}";
    }
}
