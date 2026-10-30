// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Turns a prepared run plus the caller's parameter values into the run's effective definition:
/// resolves the template at the pipeline's organization, flattens its jobs into stages, and works out
/// which servers could host them.
/// </summary>
public interface IPipelineRunParameterResolver
{
    /// <summary>Applies <paramref name="parameters"/> to <paramref name="preparation"/> and returns the
    /// preparation carrying the resolved snapshot, definition, stages and candidate targets.</summary>
    /// <exception cref="BadRequestException">A required parameter is missing or invalid.</exception>
    Task<PipelineRunPreparation> ResolveRunParametersAsync(
        PipelineRunPreparation preparation,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct = default);

    /// <summary>The distinct servers that could host any stage of <paramref name="definition"/>.</summary>
    /// <param name="projectId">The run's project, in which an <c>environment:</c> selector is read, exactly as
    /// dispatch reads it (neighbour of BACK-018).</param>
    Task<IReadOnlyCollection<int>> ResolveCandidateTargetServerIdsAsync(
        PipelineYamlDefinition definition, int? organizationId, int? projectId, CancellationToken ct);
}

/// <summary>
/// The parameter/target resolution step, extracted from <see cref="PipelineRunService"/>. It is a leaf:
/// it reads the pipeline's organization and its candidate servers and returns a value, touching no run
/// state and dispatching nothing. Extracting it is what lets
/// <see cref="PipelineCheckpointReuseService"/> compare a checkpoint against the definition the current
/// run would resolve to, without depending on the engine.
/// </summary>
public sealed class PipelineRunParameterResolver(
    IPipelineRepository repo,
    IPipelineTemplateResolver templateResolver,
    IPipelineDispatchServerResolver dispatchServers) : IPipelineRunParameterResolver
{
    public async Task<PipelineRunPreparation> ResolveRunParametersAsync(
        PipelineRunPreparation preparation,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!PipelineParameterResolver.TryResolveProblems(
                preparation.Definition.Parameters, parameters, out var effectiveParameters, out var errors))
            throw PipelineProblem.Refusal(errors);

        var organizationId = await repo.GetPipelineOrganizationIdAsync(preparation.PipelineId, ct)
            .ConfigureAwait(false) ?? 0;
        var resolution = await templateResolver.ResolveAsync(
            preparation.YamlSnapshot, organizationId, effectiveParameters, ct).ConfigureAwait(false);
        var effectiveStages = YamlParsingHelper.FlattenJobs(resolution.Definition);
        var targetIds = await ResolveCandidateTargetServerIdsAsync(
            resolution.Definition, organizationId, preparation.EffectiveProjectId, ct).ConfigureAwait(false);
        return preparation with
        {
            YamlSnapshot = resolution.Yaml,
            Definition = resolution.Definition,
            EffectiveStages = effectiveStages,
            TargetServerIds = targetIds
        };
    }

    public async Task<IReadOnlyCollection<int>> ResolveCandidateTargetServerIdsAsync(
        PipelineYamlDefinition definition, int? organizationId, int? projectId, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
        {
            // BACK-021: a selector still holding a parameter expression matches no server by name, which
            // read as "nothing to authorize". Whatever value it receives later stays inside the
            // organization's eligible servers, so that whole set is what it must be authorized against.
            var unresolved = IsUnresolved(stage.Pool) || IsUnresolved(stage.Environment) || IsUnresolved(stage.Agent);
            var candidates = await repo.FindCandidateTargetServerIdsAsync(
                unresolved ? null : stage.Pool, unresolved ? null : stage.Environment, unresolved ? null : stage.Agent,
                OsTypeHelper.Parse(stage.Os), organizationId, dispatchServers.StageHasDeployStep(stage), projectId, ct)
                .ConfigureAwait(false);
            ids.UnionWith(candidates);
        }
        return ids;
    }

    private static bool IsUnresolved(string? selector) =>
        selector?.Contains("${{", StringComparison.Ordinal) == true;
}
