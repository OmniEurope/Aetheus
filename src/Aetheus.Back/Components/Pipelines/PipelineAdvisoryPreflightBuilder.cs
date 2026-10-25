// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The ADVISORY preflight behind the launch dialog: per stage, which runner would take it and why
/// not, plus the resolution warnings.
///
/// It never refuses anything (that is <see cref="IPipelineRunPreflightService"/>'s job), it only
/// shows the user what a launch would do, and it shows the whole chain: an orchestrator's own stages
/// are four trigger steps, so listing only those previewed a run whose actual work was invisible.
/// </summary>
public interface IPipelineAdvisoryPreflightBuilder
{
    Task<PipelinePreflightDto> BuildAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        List<string> warnings,
        int? organizationId,
        int? projectId,
        CancellationToken ct);
}

public sealed class PipelineAdvisoryPreflightBuilder(
    IPipelineRepository repo,
    IPipelineDispatchServerResolver dispatchServers,
    IPipelineChildPipelineResolver children,
    ILogger<PipelineAdvisoryPreflightBuilder> logger) : IPipelineAdvisoryPreflightBuilder
{
    public async Task<PipelinePreflightDto> BuildAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        List<string> warnings,
        int? organizationId,
        int? projectId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resolvedVariables);

        var stages = new List<PreflightStageDto>();
        await AppendStagesAsync(
            definition, resolvedVariables, organizationId, projectId, stages,
            depth: 0, pipelineName: null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct)
            .ConfigureAwait(false);

        return new PipelinePreflightDto { Stages = stages, Warnings = warnings };
    }

    /// <summary>
    /// One pipeline's stages, then the stages of every pipeline it triggers, marked by depth.
    ///
    /// Best-effort by construction: a child that cannot be loaded or resolved is simply not listed.
    /// This never refuses anything, so a gap here costs a missing line in a preview, and refusing a
    /// launch because a PREVIEW could not be built would be the wrong trade entirely.
    /// </summary>
    private async Task AppendStagesAsync(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolvedVariables,
        int? organizationId,
        int? projectId,
        List<PreflightStageDto> stages,
        int depth,
        string? pipelineName,
        HashSet<string> visited,
        CancellationToken ct)
    {
        foreach (var declaredStage in YamlParsingHelper.FlattenJobs(definition))
        {
            var (stage, emptyParameter, unresolvedExpression) = PipelineStageTargetParameters.Resolve(
                declaredStage, resolvedVariables, definition.Parameters);
            // A triggered pipeline's parameters arrive from its trigger step at run time: its target
            // cannot be read here, and listing it under its raw expression would be a false finding.
            if (unresolvedExpression && depth > 0) continue;

            // An empty target parameter is reported as such: no server is chosen, so none is searched.
            var server = emptyParameter is not null ? null
                : await dispatchServers.ResolveServerForTargetAsync(
                    stage, resolvedVariables, organizationId, dispatchServers.StageHasDeployStep(stage), projectId, ct)
                    .ConfigureAwait(false);
            stages.Add(DescribeStage(stage, emptyParameter, server, depth, pipelineName));
        }

        if (projectId is not { } id || depth >= PipelineChildPipelineResolver.MaxChildDepth) return;

        foreach (var reference in children.References(definition))
        {
            if (!visited.Add(reference.Name)) continue;
            var child = await LoadChildAsync(reference, resolvedVariables, organizationId, id, ct)
                .ConfigureAwait(false);
            if (child is null) continue;
            await AppendStagesAsync(
                child.Value.Definition, child.Value.Variables, organizationId, id, stages,
                depth + 1, reference.Name, visited, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The preview line of one stage: its target, the server found for it, or why none was.</summary>
    private PreflightStageDto DescribeStage(
        PipelineStageDefinition stage,
        PipelineTemplateParameterDefinition? emptyParameter,
        Server? server,
        int depth,
        string? pipelineName)
    {
        var (kind, label) = PipelineRunHelpers.DescribeTarget(stage);
        return new PreflightStageDto
        {
            StageName = stage.Name,
            TargetKind = kind,
            Target = emptyParameter?.Name ?? label,
            Resolved = server is not null,
            ServerName = server?.Name,
            Reason = server is not null ? null
                : emptyParameter is not null
                    ? dispatchServers.BuildEmptyTargetParameterReason(stage.Name, emptyParameter.Name)
                    : dispatchServers.BuildNoServerReason(stage.Name, stage),
            Depth = depth,
            PipelineName = pipelineName,
            EmptyTargetParameter = emptyParameter is null
                ? null
                : new PreflightEmptyParameterDto(
                    emptyParameter.Name,
                    string.IsNullOrWhiteSpace(emptyParameter.DisplayName) ? emptyParameter.Name : emptyParameter.DisplayName!,
                    string.IsNullOrWhiteSpace(emptyParameter.DisplayNameFr) ? null : emptyParameter.DisplayNameFr)
        };
    }

    /// <summary>A child's definition and variables for the preview, or null when anything about it
    /// cannot be resolved. Swallowing here is deliberate: see the caller.</summary>
    private async Task<(PipelineYamlDefinition Definition, IReadOnlyDictionary<string, string> Variables)?>
        LoadChildAsync(
            ChildPipelineReference reference,
            IReadOnlyDictionary<string, string> parentVariables,
            int? organizationId,
            int projectId,
            CancellationToken ct)
    {
        try
        {
            var target = await repo.FindPipelineByNameAndProjectAsync(reference.Name, projectId, ct)
                .ConfigureAwait(false);
            if (target is null) return null;

            var problems = new List<string>();
            var definition = await children.ResolveDefinitionAsync(
                target, organizationId, reference.Name, problems, ct).ConfigureAwait(false);
            if (definition is null) return null;

            var variables = await children.ResolveVariablesAsync(
                definition, target, reference, parentVariables, projectId, reference.Name, problems, ct)
                .ConfigureAwait(false);
            return variables is null ? null : (definition, variables);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(
                exception, "Advisory preflight could not preview child pipeline '{Child}'.", reference.Name);
            return null;
        }
    }
}
