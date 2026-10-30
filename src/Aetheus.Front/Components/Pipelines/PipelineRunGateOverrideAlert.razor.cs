// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-72: the banner of a run deployed despite the quality gate. Its own component so the run page
/// stays within the file-size budget.</summary>
public partial class PipelineRunGateOverrideAlert
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunDto Run { get; set; } = default!;

    /// <summary>Who deployed despite the gate, when (local time), and the reasons it had at the launch.</summary>
    internal string Note => string.Format(
        L["GateOverrideRunNote"].Value,
        Run.GateOverrideBy,
        Run.GateOverrideAt is { } at ? at.ToLocalTime().ToString("g") : L["Unknown"].Value,
        Run.GateOverrideWarnings.Count == 0
            ? L["GateOverrideRunNoReason"].Value
            : string.Join(" ; ", Run.GateOverrideWarnings.Select(warning => DeploymentGateWarningText.Describe(warning, L))));
}
