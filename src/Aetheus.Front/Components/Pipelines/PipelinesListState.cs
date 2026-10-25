// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Retain the pipeline list and filters in PageStateCache so return navigation restores
/// the user's context.</summary>
internal sealed record PipelinesListState(
    List<PipelineDependencyDto> Pipelines,
    List<PipelineRunDto> RecentRuns,
    Dictionary<int, PipelineFleetItemDto> FleetItems,
    List<PipelineTemplateSummaryDto> TemplateOptions,
    bool FleetAvailable,
    HashSet<int> FavoritePipelineIds,
    int TotalCount,
    string? Search,
    IReadOnlyList<PipelineTriggerType> TriggerFilters,
    IReadOnlyList<int> TemplateFilterIds,
    IReadOnlyList<PipelineCatalogStatus> StatusFilters,
    bool FavoritesOnly,
    bool WithChildrenOnly);
