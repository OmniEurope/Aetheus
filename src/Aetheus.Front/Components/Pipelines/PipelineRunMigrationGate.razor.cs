// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The expand/contract gate verdicts of a run (recette R1-43), read from the run's endpoint. Read when
/// the run is first shown and again when its status changes, since the migrate step reports its verdict
/// as it completes.
/// </summary>
public partial class PipelineRunMigrationGate
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;

    private List<PipelineRunMigrationGateDto>? _gates;
    private (int RunId, PipelineStatus Status)? _loadedFor;

    protected override async Task OnParametersSetAsync()
    {
        var key = (Run.Id, Run.Status);
        if (_loadedFor == key) return;
        _loadedFor = key;
        try
        {
            _gates = await Api.Pipelines.GetPipelineRunMigrationGateAsync(Run.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // The step log still carries the gate's lines; only this summary is missing.
            _gates = null;
        }
    }

    private string StepLabel(PipelineRunMigrationGateDto gate) =>
        string.IsNullOrEmpty(gate.StageName) ? gate.StepName ?? L["MigrationGateTitle"] : $"{gate.StageName} / {gate.StepName}";

    private string Summary(PipelineRunMigrationGateDto gate) => gate.Outcome switch
    {
        MigrationGateOutcome.NoPending => L["MigrationGateSummaryNoPending"],
        MigrationGateOutcome.InitialCreation => string.Format(L["MigrationGateSummaryInitialCreation"].Value, gate.PendingCount),
        MigrationGateOutcome.Compatible => string.Format(L["MigrationGateSummaryCompatible"].Value, gate.PendingCount),
        MigrationGateOutcome.Refused => string.Format(
            L["MigrationGateSummaryRefused"].Value, gate.Migrations.Count(m => !m.Compatible), gate.PendingCount),
        _ => L["MigrationGateSummaryUnavailable"]
    };

    internal static OmniTone OutcomeTone(MigrationGateOutcome outcome) => outcome switch
    {
        MigrationGateOutcome.Compatible => OmniTone.Success,
        MigrationGateOutcome.InitialCreation => OmniTone.Accent,
        MigrationGateOutcome.NoPending => OmniTone.Neutral,
        _ => OmniTone.Danger
    };
}
