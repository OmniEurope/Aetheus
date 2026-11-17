// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R-9: the variables a pipeline reads that nothing defines, found before a run instead of at its first stage. It
/// asks the run's own questions outside a run: the variables are resolved as a launch resolves them (libraries, vaults,
/// declared variables), then <see cref="PipelineUnresolvedVariableGuard"/> names the <c>$(NAME)</c> references nothing can
/// provide, and the environment sample's <c>#{NAME}#</c> placeholders (the run's "Environment template variable(s) not
/// defined") are read against the same resolution.
/// </summary>
internal sealed class PipelineUndefinedVariablesCheck(
    IPipelineVariableResolver variables, PipelineEnvTemplateNames envTemplateNames,
    IPipelineRequirementsChecker requirements, ILogger logger)
{
    /// <summary>One blocking finding naming every undefined variable of <paramref name="definitions"/>, or null when
    /// every name is defined. R-56: each name also says where it is created, see <see cref="HolderOf"/>.</summary>
    public async Task<PipelineSetupReadinessCheckDto?> CheckAsync(
        IReadOnlyList<PipelineYamlDefinition> definitions, IReadOnlyList<(int? Id, string Name)> pipelines,
        int projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(pipelines);
        // The first pipeline that reads a name decides its holder: its own libraries and vaults are the ones its run reads.
        var readBy = new SortedDictionary<string, (PipelineYamlDefinition Definition, int? PipelineId, string PipelineName)>(
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < definitions.Count; index++)
            foreach (var name in await UndefinedOfAsync(definitions[index], projectId, ct).ConfigureAwait(false))
            {
                var (id, pipelineName) = index < pipelines.Count ? pipelines[index] : (null, definitions[index].Name);
                readBy.TryAdd(name, (definitions[index], id, pipelineName));
            }
        if (readBy.Count == 0) return null;
        // R-25: a key a library or vault of the project already holds is not "to create": the pipeline just does not declare it.
        var existing = await requirements.FindKeyHoldersAsync([.. readBy.Keys], projectId, ct).ConfigureAwait(false);

        var libraryIds = await requirements.FindLibraryIdsAsync(
            [.. definitions.SelectMany(definition => definition.VariableLibraries).Distinct(StringComparer.OrdinalIgnoreCase)],
            projectId, ct).ConfigureAwait(false);
        var vaultIds = await requirements.FindVaultIdsAsync(
            [.. definitions.SelectMany(definition => definition.Vaults).Distinct(StringComparer.OrdinalIgnoreCase)],
            projectId, ct).ConfigureAwait(false);
        return new PipelineSetupReadinessCheckDto
        {
            Kind = PipelineSetupReadinessKind.UndefinedVariables,
            Severity = PipelineSetupReadinessSeverity.Blocking,
            Items = [.. readBy.Keys],
            UndefinedVariables =
            [
                .. readBy.Select(pair => WithExisting(
                    HolderOf(pair.Key, pair.Value.Definition, libraryIds, vaultIds) with
                    {
                        PipelineId = pair.Value.PipelineId,
                        PipelineName = pair.Value.PipelineName
                    },
                    pair.Value.Definition, existing))
            ]
        };
    }

    /// <summary>
    /// R-25: the project's own library or vault that already holds the key, when this pipeline does not list it (a listed
    /// one that held the key would have defined it). The holder of the kind the name calls for wins, then the other kind;
    /// the project's own comes first (the order the repositories return). Null fields when the key exists nowhere.
    /// </summary>
    private static PipelineSetupReadinessUndefinedVariableDto WithExisting(
        PipelineSetupReadinessUndefinedVariableDto row, PipelineYamlDefinition definition,
        IReadOnlyList<PipelineKeyHolder>? holders)
    {
        var found = (holders ?? [])
            .Where(holder => string.Equals(holder.Key, row.Name, StringComparison.OrdinalIgnoreCase)
                && !(holder.Kind == PipelineSetupReadinessContainerKind.Vault ? definition.Vaults : definition.VariableLibraries)
                    .Contains(holder.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(holder => holder.Kind == row.Container ? 0 : 1)
            .FirstOrDefault();
        return found is null
            ? row
            : row with { ExistingHolderId = found.Id, ExistingHolderName = found.Name, ExistingHolderKind = found.Kind };
    }

    /// <summary>
    /// R-56: where an undefined variable is created. A name made of a secret word (PASSWORD, KEY, TOKEN, SECRET...,
    /// <see cref="ProductionVariableClassifier.IsSecretName"/>, the rule the production setup already uses to put a secret
    /// in a vault) goes to a vault, any other to a variable library. The holder is the one the pipeline itself lists
    /// (<c>vaults:</c> or <c>variable_libraries:</c>): the first that exists, else the first listed (to create), else none
    /// (the pipeline lists nothing of that kind: the name is empty and the page says so rather than guessing one).
    /// </summary>
    private static PipelineSetupReadinessUndefinedVariableDto HolderOf(
        string name, PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, int>? libraryIds, IReadOnlyDictionary<string, int>? vaultIds)
    {
        var secret = ProductionVariableClassifier.IsSecretName(name);
        var listed = secret ? definition.Vaults : definition.VariableLibraries;
        var ids = secret ? vaultIds : libraryIds;
        var holder = listed.FirstOrDefault(candidate => ids is not null && ids.ContainsKey(candidate)) ?? listed.FirstOrDefault();
        return new PipelineSetupReadinessUndefinedVariableDto
        {
            Name = name,
            Container = secret ? PipelineSetupReadinessContainerKind.Vault : PipelineSetupReadinessContainerKind.Library,
            OwnerName = holder ?? string.Empty,
            OwnerId = holder is not null && ids is not null && ids.TryGetValue(holder, out var id) ? id : null
        };
    }

    /// <summary>
    /// R-25: the variables a launch of <paramref name="definition"/> resolves, for the readiness to tell which steps will not
    /// run. Null when the resolution refuses the definition (an invalid deployment target): the caller then keeps every step.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> ReadSampleNamesAsync(
        PipelineYamlDefinition definition, int projectId, CancellationToken ct) =>
        await envTemplateNames.ReadAsync(
            definition, projectId, ct, await ResolveLaunchVariablesAsync(definition, projectId, ct).ConfigureAwait(false))
            .ConfigureAwait(false);

    /// <summary>R-44: the variables a launch of each definition resolves, kept by definition instance for the life of
    /// this check (one readiness verdict). The empty-value check and the undefined-variable check ask the same question of
    /// the same definition, and each resolution costs the commands of its variable sources; a refusal is kept as null.</summary>
    private readonly Dictionary<PipelineYamlDefinition, (Dictionary<string, string> Resolved, HashSet<string> SecretKeys)?> _resolved =
        new(ReferenceEqualityComparer.Instance);

    private async Task<(Dictionary<string, string> Resolved, HashSet<string> SecretKeys)?> ResolveOnceAsync(
        PipelineYamlDefinition definition, int projectId, CancellationToken ct)
    {
        if (_resolved.TryGetValue(definition, out var cached)) return cached;
        try
        {
            var (resolved, _, secretKeys) = await variables
                .ResolveVariablesWithWarningsAsync(definition, projectId, null, ct).ConfigureAwait(false);
            return _resolved[definition] = (resolved, secretKeys);
        }
        catch (BadRequestException exception)
        {
            // Mandatory: the resolution refuses a definition whose deployment target is invalid, and that refusal is
            // another finding of the launch. It must not take the whole readiness verdict down with it.
            logger.LogDebug(exception, "Readiness could not resolve the variables of '{Pipeline}'.", definition.Name);
            return _resolved[definition] = null;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> ResolveLaunchVariablesAsync(
        PipelineYamlDefinition definition, int projectId, CancellationToken ct) =>
        (await ResolveOnceAsync(definition, projectId, ct).ConfigureAwait(false))?.Resolved;

    private async Task<IReadOnlyCollection<string>> UndefinedOfAsync(
        PipelineYamlDefinition definition, int projectId, CancellationToken ct)
    {
        if (await ResolveOnceAsync(definition, projectId, ct).ConfigureAwait(false) is not { } launch)
            return [];
        var (resolved, secretKeys) = launch;

        var undefined = new HashSet<string>(
            PipelineUnresolvedVariableGuard.FindUnprovidableNames(definition, resolved, secretKeys).Keys,
            StringComparer.OrdinalIgnoreCase);
        // The run renders the environment sample with the variables of the run: a placeholder none of them holds, that no
        // stage declares and that the run does not provide later, stops the deployment by name.
        var providedLater = PipelineUnresolvedVariableGuard.BuildDeferredNames(definition);
        var declaredByAStage = YamlParsingHelper.FlattenJobs(definition)
            .SelectMany(stage => stage.Variables.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // R-25: a sample read only by a step that will not run on these variables (blue-green only, on a replace
        // deployment) asks for nothing.
        foreach (var name in await envTemplateNames.ReadAsync(definition, projectId, ct, resolved).ConfigureAwait(false))
            if (!resolved.ContainsKey(name) && !providedLater.Contains(name) && !declaredByAStage.Contains(name))
                undefined.Add(name);
        return undefined;
    }
}
