// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class PipelineDependencyGrid
{
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PipelineDependencyDto> Items { get; set; } = [];
    [Parameter, EditorRequired] public Func<int, string> PipelineHref { get; set; } = default!;
    [Parameter, EditorRequired] public Func<int, string?, Task> RunPipeline { get; set; } = default!;
    [Parameter, EditorRequired] public Func<int, bool, Task> SetFavorite { get; set; } = default!;
    [Parameter] public IReadOnlySet<int> FavoritePipelineIds { get; set; } = new HashSet<int>();
    [Parameter] public IReadOnlySet<int> PendingFavoritePipelineIds { get; set; } = new HashSet<int>();
    [Parameter]
    public IReadOnlyDictionary<int, PipelineFleetItemDto> FleetItems { get; set; }
        = new Dictionary<int, PipelineFleetItemDto>();
    [Parameter] public bool FleetAvailable { get; set; }
    [Parameter] public bool GroupByProject { get; set; }
    /// <summary>The grid ends the page: it takes the height left under it (OE FillAvailableHeight) and
    /// is the one scroll area, instead of paging.</summary>
    [Parameter] public bool FillHeight { get; set; }
    [Parameter] public bool Virtualize { get; set; }
    [Parameter] public bool CanWrite { get; set; }
    [Parameter] public int? ServerId { get; set; }

    private IReadOnlyList<PipelineDependencyDto> DisplayItems => Items;

    // Recette R-213: header filters of the catalogue. The model and status columns have no property of
    // their own, so they carry a key and read the fleet data; every delegate is built once so the
    // columns see the same one on every render.
    internal const string TemplateColumn = "Template";
    internal const string StatusColumn = "CatalogStatus";
    private static readonly string[] AllStatusCandidates =
        [.. PipelineCatalogView.Statuses(fleetAvailable: true).Select(status => status.ToString())];
    private static readonly string[] RunStatusCandidates =
        [.. PipelineCatalogView.Statuses(fleetAvailable: false).Select(status => status.ToString())];
    private Func<string, string>? _triggerText;
    private Func<string, string>? _statusText;
    private Func<PipelineDependencyDto, object?>? _templateValue;
    private Func<PipelineDependencyDto, string, bool>? _statusPredicate;
    private Func<string, string> TriggerText => _triggerText ??= GridFilterText.ForEnum<PipelineTriggerType>(L);
    private IReadOnlyList<string> StatusCandidates => FleetAvailable ? AllStatusCandidates : RunStatusCandidates;
    private Func<string, string> StatusText => _statusText ??= value =>
        Enum.TryParse<PipelineCatalogStatus>(value, ignoreCase: true, out var status)
            ? L[PipelineCatalogView.LabelKey(status)].Value
            : value;
    private Func<PipelineDependencyDto, object?> TemplateValue => _templateValue ??= pipeline =>
        FleetItem(pipeline.Id) is { } fleetItem
            ? string.IsNullOrWhiteSpace(fleetItem.TemplateName) ? L["AutonomousPipeline"].Value : fleetItem.TemplateName
            : null;
    private Func<PipelineDependencyDto, string, bool> StatusPredicate => _statusPredicate ??= (pipeline, encoded) =>
        PipelineCatalogView.MatchesAny(pipeline, ParseStatuses(encoded), FleetItems);

    /// <summary>The chosen statuses of the multi-select filter; an unknown value is ignored.</summary>
    internal static IReadOnlyCollection<PipelineCatalogStatus> ParseStatuses(string? encoded) =>
        [.. OmniDataGridFilterValues.Split(encoded)
            .Select(value => Enum.TryParse<PipelineCatalogStatus>(value, ignoreCase: true, out var status) ? status : (PipelineCatalogStatus?)null)
            .OfType<PipelineCatalogStatus>()
            .Distinct()];
    private string GridCssClass => $"pipeline-dependency-grid aetheus-clickable-rows{(FillHeight ? " pipeline-grid-fill" : string.Empty)}{(Virtualize ? " pipeline-grid-virtualized pipeline-grid-five-rows" : string.Empty)}";

    /// <summary>Recette R-121: decided per row, not per grid. A row expands only when it launches
    /// other pipelines; a leaf shows no chevron.</summary>
    internal static bool HasChildren(PipelineDependencyDto pipeline) => pipeline.References.Count > 0;

    private bool IsFavorite(int pipelineId) => FavoritePipelineIds.Contains(pipelineId);

    private PipelineFleetItemDto? FleetItem(int pipelineId) => FleetItems.GetValueOrDefault(pipelineId);

    private string FreshnessText(PipelineFleetFreshness freshness) => freshness switch
    {
        PipelineFleetFreshness.Current => L["Current"],
        PipelineFleetFreshness.Outdated => L["Outdated"],
        _ => L["AutonomousPipeline"]
    };

    private static OmniTone FreshnessStyle(PipelineFleetFreshness freshness) => freshness switch
    {
        PipelineFleetFreshness.Current => OmniTone.Success,
        // Recette R2-145: a pinned version is a choice (aetheus-candidate pins application-candidate@1 on
        // purpose), not a delay: a newer version in the catalogue is information, not a warning.
        PipelineFleetFreshness.Outdated => OmniTone.Info,
        _ => OmniTone.Neutral
    };

    private string TemplateStatusTitle(PipelineFleetItemDto item) => item.LatestVersion is { } latestVersion
        ? $"{FreshnessText(item.Freshness)} · v{item.PinnedVersion?.ToString() ?? "-"} → v{latestVersion}"
        : FreshnessText(item.Freshness);

    private void UpdatePipelineTemplate(int pipelineId, int latestVersion) =>
        Nav.NavigateTo($"/pipelines/{pipelineId}/template/update/{latestVersion}");

    private void OnRowRender(OmniDataGridRowRenderArgs<PipelineDependencyDto> args) =>
        args.Expandable = HasChildren(args.Item);

    private void OnRowClick(PipelineDependencyDto pipeline) =>
        Nav.NavigateTo(PipelineHref(pipeline.Id));

    private void EditPipeline(int id)
    {
        var href = PipelineHref(id);
        Nav.NavigateTo($"{href}{(href.Contains('?') ? '&' : '?')}tab=edit");
    }
}
