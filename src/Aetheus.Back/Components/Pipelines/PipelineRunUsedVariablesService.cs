// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Shared.Components.Projects;

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineRunUsedVariablesService
{
    /// <summary>The variables a run read from libraries and vaults, each with its source; null for an unknown run.</summary>
    Task<List<PipelineRunUsedVariableDto>?> GetUsedVariablesAsync(int runId, CancellationToken ct = default);
}

/// <summary>
/// R-37: which library or vault each variable a run read comes from. The libraries and vaults are the ones the run's
/// own definition snapshot names (<c>variable_libraries:</c>, <c>vaults:</c> and <c>requires:</c>, with the project
/// slug expanded); the source of a key is the one that wins under the precedence a launch applies. A library
/// entry carries the value the run recorded. A vault secret never carries its value, only whether it is available:
/// the clear value is read here only to know whether it is empty, and dropped.
/// </summary>
public sealed class PipelineRunUsedVariablesService(
    IPipelineRunUsedVariablesReader reader,
    IVariableLibraryService libraries,
    IVaultService vaults) : IPipelineRunUsedVariablesService
{
    public async Task<List<PipelineRunUsedVariableDto>?> GetUsedVariablesAsync(int runId, CancellationToken ct = default)
    {
        var source = await reader.GetSourceAsync(runId, ct).ConfigureAwait(false);
        if (source is null) return null;
        if (source.ProjectId is not { } projectId) return [];
        if (PipelineAdvisorySteps.ParseSnapshot(source.YamlSnapshot) is not { } definition) return [];

        var slug = source.ProjectName is null ? null : ProjectSlugs.Of(source.ProjectName, projectId);
        var libraryNames = Names(definition.VariableLibraries.Concat(definition.Requires?.Libraries ?? []), slug);
        var vaultNames = Names(definition.Vaults.Concat(definition.Requires?.Vaults ?? []), slug);
        var recorded = PipelineRunHelpers.DeserializeResolvedVariables(source.ResolvedVariablesJson);

        var rows = new List<PipelineRunUsedVariableDto>();
        rows.AddRange(await LibraryRowsAsync(libraryNames, projectId, recorded, ct).ConfigureAwait(false));
        rows.AddRange(await VaultRowsAsync(vaultNames, projectId, ct).ConfigureAwait(false));
        return rows;
    }

    private static List<string> Names(IEnumerable<string> declared, string? slug) =>
        [.. declared.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => ProjectSlugs.Expand(name, slug))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private async Task<List<PipelineRunUsedVariableDto>> LibraryRowsAsync(
        List<string> names, int projectId, Dictionary<string, string> recorded, CancellationToken ct)
    {
        if (names.Count == 0) return [];
        var owners = await libraries.ResolveEntryOwnersWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        var ids = await libraries.ResolveLibraryIdsWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        var sourceNames = await SourceNamesAsync(
            owners.Values, ids, async id => (await libraries.GetLibraryDetailAsync(id, ct).ConfigureAwait(false))?.Name)
            .ConfigureAwait(false);
        return [.. owners.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            recorded.TryGetValue(pair.Key, out var value);
            return new PipelineRunUsedVariableDto
            {
                Kind = PipelineRunResourceKind.Library,
                SourceId = pair.Value,
                SourceName = sourceNames.GetValueOrDefault(pair.Value, string.Empty),
                Name = pair.Key,
                Value = value,
                IsAvailable = !string.IsNullOrEmpty(value)
            };
        })];
    }

    private async Task<List<PipelineRunUsedVariableDto>> VaultRowsAsync(
        List<string> names, int projectId, CancellationToken ct)
    {
        if (names.Count == 0) return [];
        var (secrets, _) = await vaults.ResolveVaultSecretsWithCrossAccessAndNamesAsync(names, projectId, ct).ConfigureAwait(false);
        var owners = await vaults.ResolveSecretOwnersWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        var ids = await vaults.ResolveVaultIdsWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        var sourceNames = await SourceNamesAsync(
            owners.Values, ids, async id => (await vaults.GetVaultDetailAsync(id, ct).ConfigureAwait(false))?.Name)
            .ConfigureAwait(false);
        return [.. owners.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new PipelineRunUsedVariableDto
        {
            Kind = PipelineRunResourceKind.Vault,
            SourceId = pair.Value,
            SourceName = sourceNames.GetValueOrDefault(pair.Value, string.Empty),
            Name = pair.Key,
            // Never the value: the secret was decrypted above only to learn whether it is empty.
            Value = null,
            IsAvailable = secrets.TryGetValue(pair.Key, out var secret) && !string.IsNullOrEmpty(secret)
        })];
    }

    /// <summary>The name of each owning library or vault: read off the declared names when they identify it, asked
    /// once per owner otherwise (two sources of one name in different scopes).</summary>
    private static async Task<Dictionary<int, string>> SourceNamesAsync(
        IEnumerable<int> ownerIds, Dictionary<string, int> idsByName, Func<int, Task<string?>> nameOf)
    {
        var names = new Dictionary<int, string>();
        foreach (var (name, id) in idsByName) names.TryAdd(id, name);
        foreach (var id in ownerIds.Distinct().Where(id => !names.ContainsKey(id)))
            if (await nameOf(id).ConfigureAwait(false) is { Length: > 0 } name)
                names[id] = name;
        return names;
    }
}
