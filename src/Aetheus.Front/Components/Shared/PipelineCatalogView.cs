// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The pure rules behind the pipelines catalogue (recette R-119, R-121, R-122): which rows a status
/// chip matches, how many rows each chip counts, and the default order that puts the starred
/// pipelines first. Kept out of <see cref="PipelinesList"/> so the rules are testable on their own
/// and the component stays under the file-size budget.
/// </summary>
internal static class PipelineCatalogView
{
    /// <summary>Statuses shown by the summary strip, in display order. Outdated needs the fleet data.</summary>
    /// <remarks>Recette R-167: every freshness state of the fleet is there, not only Outdated.</remarks>
    public static IReadOnlyList<PipelineCatalogStatus> Statuses(bool fleetAvailable) => fleetAvailable
        ? [PipelineCatalogStatus.Failed, PipelineCatalogStatus.Running, PipelineCatalogStatus.NeverRun,
           PipelineCatalogStatus.Outdated, PipelineCatalogStatus.Current, PipelineCatalogStatus.OffCatalog]
        : [PipelineCatalogStatus.Failed, PipelineCatalogStatus.Running, PipelineCatalogStatus.NeverRun];

    /// <summary>
    /// Recette R-167: a row passes when no status is chosen or when it matches ANY chosen one, the
    /// usual reading of a multiple choice.
    /// </summary>
    public static bool MatchesAny(
        PipelineDependencyDto pipeline,
        IReadOnlyCollection<PipelineCatalogStatus> statuses,
        IReadOnlyDictionary<int, PipelineFleetItemDto> fleetItems) =>
        statuses.Count == 0 || statuses.Any(status => Matches(pipeline, status, fleetItems));

    public static bool Matches(
        PipelineDependencyDto pipeline,
        PipelineCatalogStatus status,
        IReadOnlyDictionary<int, PipelineFleetItemDto> fleetItems)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(fleetItems);
        // RecentRuns is newest first (see PipelineDependencyDto), so its head is the latest run.
        var latest = pipeline.RecentRuns.FirstOrDefault();
        return status switch
        {
            PipelineCatalogStatus.Failed => latest?.Status == PipelineStatus.Failed,
            PipelineCatalogStatus.Running => latest?.Status is PipelineStatus.Pending
                or PipelineStatus.Running or PipelineStatus.WaitingForApproval,
            PipelineCatalogStatus.NeverRun => latest is null,
            PipelineCatalogStatus.Outdated => HasFreshness(pipeline, fleetItems, PipelineFleetFreshness.Outdated),
            PipelineCatalogStatus.Current => HasFreshness(pipeline, fleetItems, PipelineFleetFreshness.Current),
            PipelineCatalogStatus.OffCatalog => HasFreshness(pipeline, fleetItems, PipelineFleetFreshness.OffCatalog),
            _ => false
        };
    }

    private static bool HasFreshness(
        PipelineDependencyDto pipeline,
        IReadOnlyDictionary<int, PipelineFleetItemDto> fleetItems,
        PipelineFleetFreshness freshness) =>
        fleetItems.TryGetValue(pipeline.Id, out var fleetItem) && fleetItem.Freshness == freshness;

    /// <summary>The resource key of a status's label, shared by the summary chips and the catalogue
    /// grid's status filter (recette R-213) so both name a status the same way.</summary>
    public static string LabelKey(PipelineCatalogStatus status) => status switch
    {
        PipelineCatalogStatus.Failed => "Failed",
        PipelineCatalogStatus.Running => "Running",
        PipelineCatalogStatus.NeverRun => "PipelineNeverRun",
        PipelineCatalogStatus.Current => "Current",
        PipelineCatalogStatus.OffCatalog => "OffCatalog",
        _ => "Outdated"
    };

    /// <summary>
    /// Recette R-165: each chip has its colour (danger, info, warning, primary, neutral), on top of its
    /// icon, label and count, never instead of them. "Up to date" is blue, not green: STD-BTN (revised
    /// 2026-09-28) never puts the success colour on a button.
    /// </summary>
    public static OmniButtonVariant Variant(PipelineCatalogStatus status) => status switch
    {
        PipelineCatalogStatus.Failed => OmniButtonVariant.Danger,
        PipelineCatalogStatus.Running => OmniButtonVariant.Info,
        PipelineCatalogStatus.Outdated => OmniButtonVariant.Warning,
        PipelineCatalogStatus.Current => OmniButtonVariant.Primary,
        _ => OmniButtonVariant.Secondary
    };

    /// <summary>The chip's icon: with its label and count, the chip never relies on colour alone.</summary>
    public static OmniIconName Icon(PipelineCatalogStatus status) => status switch
    {
        PipelineCatalogStatus.Failed => OmniIconName.Error,
        PipelineCatalogStatus.Running => OmniIconName.Play,
        PipelineCatalogStatus.NeverRun => OmniIconName.Hourglass,
        PipelineCatalogStatus.Current => OmniIconName.CheckCircle,
        PipelineCatalogStatus.OffCatalog => OmniIconName.LinkBreak,
        _ => OmniIconName.Warning
    };

    public static string ChipCssClass(PipelineCatalogStatus status, bool active) =>
        $"pipeline-status-chip pipeline-status-chip-{status.ToString().ToLowerInvariant()}{(active ? " pipeline-status-chip-active" : string.Empty)}";

    public static int Count(
        IEnumerable<PipelineDependencyDto> pipelines,
        PipelineCatalogStatus status,
        IReadOnlyDictionary<int, PipelineFleetItemDto> fleetItems) =>
        pipelines.Count(pipeline => Matches(pipeline, status, fleetItems));

    /// <summary>The catalogue's order before any column sort: pipelines with a run in progress or
    /// awaiting an approval first, the most recently started first (recette R2-093), then the starred
    /// ones (the favourites panel is gone, the favourites lead the one table instead, R-119), then
    /// project, then name. A column sort chosen in the grid reorders the rows over this.</summary>
    public static List<PipelineDependencyDto> DefaultOrder(
        IEnumerable<PipelineDependencyDto> pipelines,
        IReadOnlySet<int> favoritePipelineIds) => pipelines
        .OrderByDescending(pipeline => ActiveRunStartedAt(pipeline).HasValue)
        .ThenByDescending(ActiveRunStartedAt)
        .ThenByDescending(pipeline => favoritePipelineIds.Contains(pipeline.Id))
        .ThenBy(pipeline => pipeline.ProjectName)
        .ThenBy(pipeline => pipeline.Name)
        .ToList();

    /// <summary>When the latest of the pipeline's runs in progress started; null when none is in
    /// progress. RecentRuns holds its five newest runs.</summary>
    private static DateTime? ActiveRunStartedAt(PipelineDependencyDto pipeline) => pipeline.RecentRuns
        .Where(run => !run.Status.IsTerminal())
        .Select(run => (DateTime?)run.StartedAt)
        .Max();
}
