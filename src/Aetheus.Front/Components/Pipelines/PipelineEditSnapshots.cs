// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the pipeline page shows, kept for a return to it.</summary>
internal sealed record PipelinePageState(
    PipelineDto Pipeline, List<PipelineRunDto> FirstRuns, int RunsTotalCount, List<ProjectDto> Projects,
    List<PipelineTemplateSummaryDto> Templates, PipelineSourceDto? Source, List<string> SourceBranches,
    string? BaseTemplateYaml, PipelineFleetItemDto? FleetItem);

/// <summary>The values the runs grid of the pipeline page writes in its export: the duration and the
/// server its cells show.</summary>
internal static class PipelineRunExports
{
    public static readonly Func<PipelineRunDto, object?> Duration = run => PipelineRunFormatting.FormatDuration(run.StartedAt, run.CompletedAt);

    public static readonly Func<PipelineRunDto, object?> Server = run =>
        run.Steps.FirstOrDefault(step => !step.IsSystem && step.ServerName is not null)?.ServerName ?? string.Empty;
}
