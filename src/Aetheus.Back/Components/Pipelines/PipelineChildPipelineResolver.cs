// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// One pipeline that a definition hands work to: a <c>type: trigger</c> step, or an
/// <c>on_success</c> entry. <see cref="ForwardedNames"/> holds the variable and parameter names the
/// parent passes down, which a preflight counts as provided whatever their value.
/// <see cref="SameSourceCommit"/> is true when the child runs on its parent's own commit (a trigger step
/// that inherits the source or pins <c>$(BUILD_SOURCEVERSION)</c>): only then can a launch judge what
/// the child resolves relative to that commit.
/// </summary>
public sealed record ChildPipelineReference(
    string Name, bool FromOnSuccess, IReadOnlyList<string> ForwardedNames, bool SameSourceCommit = false);

/// <summary>
/// Walks the chain of pipelines a definition triggers, and turns one link of that chain into a
/// resolved definition and a resolved variable set.
///
/// Both preflights need exactly this: the blocking one to refuse a launch whose fourth pipeline
/// cannot supply a variable, the advisory one to preview the stages that pipeline would run. Holding
/// it once means the two cannot disagree about what "the children of this pipeline" are.
/// </summary>
public interface IPipelineChildPipelineResolver
{
    /// <summary>Every pipeline this definition hands work to, trigger steps first then on_success.</summary>
    IEnumerable<ChildPipelineReference> References(PipelineYamlDefinition definition);

    /// <summary>The child's resolved definition, or null with a reason appended to
    /// <paramref name="problems"/>.</summary>
    Task<PipelineYamlDefinition?> ResolveDefinitionAsync(
        Pipeline target,
        int? organizationId,
        string childPath,
        List<PipelineProblem> problems,
        CancellationToken ct);

    /// <summary>The variables the child would run with, or null with a reason appended to
    /// <paramref name="problems"/>.</summary>
    Task<Dictionary<string, string>?> ResolveVariablesAsync(
        PipelineYamlDefinition childDefinition,
        Pipeline target,
        ChildPipelineReference reference,
        IReadOnlyDictionary<string, string> parentVariables,
        int projectId,
        string childPath,
        List<PipelineProblem> problems,
        CancellationToken ct);
}

public sealed class PipelineChildPipelineResolver(
    IPipelineTemplateResolver templateResolver,
    IPipelineVariableResolver variableResolver,
    IPipelineEnvironmentReader? environments = null) : IPipelineChildPipelineResolver
{
    /// <summary>How deep the chain of triggered pipelines is followed. Five is well past the deepest
    /// real chain (candidate -> ci/quality/security/qa, then deploy-prod) and bounds a definition that
    /// references itself through a name the caller has not seen yet.</summary>
    public const int MaxChildDepth = 5;

    public IEnumerable<ChildPipelineReference> References(PipelineYamlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Enumerate(definition);

        static IEnumerable<ChildPipelineReference> Enumerate(PipelineYamlDefinition definition)
        {
            foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
                foreach (var step in stage.Steps)
                {
                    if (string.IsNullOrWhiteSpace(step.Pipeline)) continue;
                    if (!string.Equals(step.Type, "trigger", StringComparison.OrdinalIgnoreCase)) continue;
                    yield return new ChildPipelineReference(
                        step.Pipeline.Trim(),
                        false,
                        [.. step.Variables.Keys, .. step.Parameters.Keys],
                        step.InheritSource
                        || string.Equals(step.SourceCommit?.Trim(), "$(BUILD_SOURCEVERSION)", StringComparison.OrdinalIgnoreCase));
                }

            foreach (var downstream in definition.OnSuccess)
            {
                if (string.IsNullOrWhiteSpace(downstream.Pipeline)) continue;
                // A downstream run receives the selected release as UPSTREAM_RELEASE, which the
                // control plane already declares, so nothing else is forwarded by name.
                yield return new ChildPipelineReference(downstream.Pipeline.Trim(), true, []);
            }
        }
    }

    public async Task<PipelineYamlDefinition?> ResolveDefinitionAsync(
        Pipeline target,
        int? organizationId,
        string childPath,
        List<PipelineProblem> problems,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(problems);

        if (string.IsNullOrWhiteSpace(target.YamlDefinition))
        {
            problems.Add(PipelineProblem.Of(
                PipelineProblemCodes.ChildPipelineNoDefinition, $"{childPath}: the pipeline has no definition.",
                (PipelineProblem.ChainParameter, childPath)));
            return null;
        }
        try
        {
            // organizationId is only used to resolve `extends:` against the immutable template store;
            // a pipeline that extends nothing never reads it.
            var resolution = await templateResolver
                .ResolveAsync(target.YamlDefinition, organizationId ?? 0, ct: ct).ConfigureAwait(false);
            // R-63: a pipeline chained from this one (the candidate's deployment) has no parameter of its own at launch: the
            // production environment's server is its default, as it is when the pipeline is launched itself.
            return await PipelineProductionServerDefault.ApplyAsync(environments, resolution.Definition, target.ProjectId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BadRequestException or NotFoundException
                                              or YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            problems.Add(PipelineProblem.Of(
                PipelineProblemCodes.ChildPipelineDefinitionUnresolvable,
                $"{childPath}: its definition cannot be resolved. {exception.Message}",
                (PipelineProblem.ChainParameter, childPath), ("detail", exception.Message)));
            return null;
        }
    }

    public async Task<Dictionary<string, string>?> ResolveVariablesAsync(
        PipelineYamlDefinition childDefinition,
        Pipeline target,
        ChildPipelineReference reference,
        IReadOnlyDictionary<string, string> parentVariables,
        int projectId,
        string childPath,
        List<PipelineProblem> problems,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(childDefinition);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(parentVariables);
        ArgumentNullException.ThrowIfNull(problems);

        var seed = new Dictionary<string, string>(parentVariables, StringComparer.OrdinalIgnoreCase);
        // Parent outputs supplied only at runtime are seeded empty for child preflight, since a
        // deferred value is not a missing variable.
        foreach (var (name, value) in parentVariables)
            if (PipelineUnresolvedVariableGuard.ReferencesAnUnresolvedName(value, parentVariables))
                seed[name] = string.Empty;
        // Everything the parent hands the child at run time counts as provided, whatever its value:
        // the question a preflight answers is whether a name CAN be provided, never what it holds.
        foreach (var name in reference.ForwardedNames) seed[name] = string.Empty;
        foreach (var parameter in childDefinition.Parameters)
            seed[parameter.Name] = parameter.Default ?? string.Empty;

        try
        {
            var (resolved, _, _) = await variableResolver.ResolveVariablesWithWarningsAsync(
                childDefinition, projectId, seed, ct,
                pipelineId: target.Id, pipelineName: target.Name,
                enforceResolvedReferences: true).ConfigureAwait(false);
            // R-23: a stage target built from a parameter (agent: ${{ parameters.deployServer }}) is read in the
            // ${{ parameters.X }} namespace, as the launch of the child does. Without it the child's runner was looked up
            // under the expression itself, and a chain to the wizard's deploy-prod was refused for "no online runner".
            // A parameter the parent forwards is only known at run time: empty, so no target is claimed for it.
            foreach (var parameter in childDefinition.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
                resolved[PipelineCommandBuilder.ParameterKeyPrefix + parameter.Name] =
                    reference.ForwardedNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase)
                        ? string.Empty
                        : parameter.Default ?? string.Empty;
            return resolved;
        }
        catch (BadRequestException exception)
        {
            // The one refusal that pays for this whole traversal: a missing variable-library entry or
            // an unreachable vault, discovered at launch instead of two hours into the chain.
            // A refusal that states its own code (a missing or empty variable) keeps it, attributed to
            // the child; any other keeps its English sentence as the detail.
            problems.Add(exception.Problems is [var stated]
                ? PipelineProblem.FromDto(stated, exception.Message).InChain(childPath)
                : PipelineProblem.Of(
                    PipelineProblemCodes.ChildPipelineVariablesRefused, $"{childPath}: {exception.Message}",
                    (PipelineProblem.ChainParameter, childPath), ("detail", exception.Message)));
            return null;
        }
    }
}
