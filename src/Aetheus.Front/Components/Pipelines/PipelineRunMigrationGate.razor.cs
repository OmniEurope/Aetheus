// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The expand/contract gate verdicts of a run (recette R1-43), shown at the top of the run's Schema tab. The
/// page reads them with the rest of the tab (<see cref="PipelineRunSchemaState"/>) and passes them in.
/// </summary>
public partial class PipelineRunMigrationGate
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PipelineRunMigrationGateDto> Gates { get; set; } = default!;

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
