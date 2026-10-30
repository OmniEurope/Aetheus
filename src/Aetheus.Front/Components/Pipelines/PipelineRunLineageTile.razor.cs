// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunLineageTile
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    /// <summary>The follow-up runs named in the tile; more open in a dialog.</summary>
    internal const int MaxShown = 3;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;
    [Parameter] public int? ProjectId { get; set; }
    [Parameter] public int? ServerId { get; set; }

    private PipelineRunLineageDto? _lineage;
    private (int RunId, PipelineStatus Status)? _loadedFor;

    private string? ParentPipeline => PipelineRunFormatting.UpstreamPipeline(Run.ResolvedVariables);
    private int? ParentRunId => PipelineRunFormatting.UpstreamRunId(Run.ResolvedVariables);

    /// <summary>The lineage tile spans two columns when it names a run or a long account, leaving
    /// room for its label and identifier.</summary>
    internal bool IsWide => ParentPipeline is not null
                            || _lineage is { Downstream.Count: > 0 }
                            || ActorText.Length > WideActorLength;

    internal const int WideActorLength = 14;

    /// <summary>The marker class tests and the wide state share; the width itself is the OE tile's
    /// <c>DoubleWidth</c>.</summary>
    private string TileClass => IsWide
        ? "run-lineage-tile run-lineage-tile--wide"
        : "run-lineage-tile";

    /// <summary>False while the lineage is read, and when it could not be: "automatic" is then unknown.</summary>
    private bool _lineageRead;

    /// <summary>The account that launched the run; "automatic" only once the lineage says nobody did.</summary>
    private string ActorText => _lineage switch
    {
        { TriggeredBy: { } actor } => actor,
        not null => L["RunLaunchedAutomatically"],
        null => _lineageRead ? L["NotAvailableShort"] : L["Loading"]
    };

    /// <summary>Read when the run is first shown and again when its status changes: a run starts the
    /// next one of its chain as it ends, so the list is only complete then.</summary>
    protected override async Task OnParametersSetAsync()
    {
        var key = (Run.Id, Run.Status);
        if (_loadedFor == key) return;
        _loadedFor = key;
        try
        {
            _lineage = await Api.Pipelines.GetPipelineRunLineageAsync(Run.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // The tile still says what the run's own variables tell (its parent); only the person
            // and the runs it started are missing.
            _lineage = null;
        }

        _lineageRead = true;
    }

    private Task ShowAllAsync() =>
        _lineage is null
            ? Task.CompletedTask
            : Dialog.OpenAsync<PipelineRunLineageDialog>(
                L["RunFollowUps"].Value,
                new Dictionary<string, object?>
                {
                    [nameof(PipelineRunLineageDialog.Runs)] = _lineage.Downstream,
                    [nameof(PipelineRunLineageDialog.ProjectId)] = ProjectId,
                    [nameof(PipelineRunLineageDialog.ServerId)] = ServerId
                },
                new OmniDialogOptions { Width = "560px", AutoFocusFirstElement = false });
}
