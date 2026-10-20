// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Keep status matching, counts and default ordering independent of PipelinesList so
/// catalogue rules can be tested without rendering a component.</summary>
internal static class PipelineCatalogView
{
    /// <summary>Expose every fleet freshness state in display order so the summary can filter the
    /// complete catalogue.</summary>
    public static IReadOnlyList<PipelineCatalogStatus> Statuses(bool fleetAvailable) => fleetAvailable
        ? [PipelineCatalogStatus.Failed, PipelineCatalogStatus.Running, PipelineCatalogStatus.NeverRun,
           PipelineCatalogStatus.Outdated, PipelineCatalogStatus.Current, PipelineCatalogStatus.OffCatalog]
        : [PipelineCatalogStatus.Failed, PipelineCatalogStatus.Running, PipelineCatalogStatus.NeverRun];

    /// <summary> A row passes when no status is chosen or when it matches ANY chosen one, the usual
    /// reading of a multiple choice. </summary>
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
    /// grid's status filter so both name a status the same way.</summary>
    public static string LabelKey(PipelineCatalogStatus status) => status switch
    {
        PipelineCatalogStatus.Failed => "Failed",
        PipelineCatalogStatus.Running => "Running",
        PipelineCatalogStatus.NeverRun => "PipelineNeverRun",
        PipelineCatalogStatus.Current => "Current",
        PipelineCatalogStatus.OffCatalog => "OffCatalog",
        _ => "Outdated"
    };

    /// <summary>Combine each chip's colour with its icon, label and count so status remains
    /// understandable without colour; STD-BTN uses blue for Up to date rather than a
    /// success-coloured button.</summary>
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

    /// <summary>Prioritize running or approval-waiting pipelines by newest start, then favourites,
    /// project and name so active work is visible first; explicit grid sorting overrides this
    /// default.</summary>
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
