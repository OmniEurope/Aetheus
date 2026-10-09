// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// What a pipelines list showed and how it was filtered, kept for a return to it (recette R2-087,
/// <see cref="PageStateCache"/>).
/// </summary>
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
