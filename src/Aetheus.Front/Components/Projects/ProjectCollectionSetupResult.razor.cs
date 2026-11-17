// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Components.Projects;

/// <summary>R-26 (user decision 2026-11-05): tells, on the project's overview where the wizard ends, what the preparation of the
/// telemetry and the audience did for each item (done, already present, failed with its reason). It reads what the wizard
/// recorded in this browser tab, never a guess: nothing is shown when the wizard prepared nothing, and a failed item is shown as
/// failed. A key is never claimed created unless the server said it created it.</summary>
public partial class ProjectCollectionSetupResult
{
    [Inject] private PipelineSetupCollectionState State { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private PipelineSetupCollectionResultDto? _result;

    protected override void OnParametersSet() => _result = State.For(ProjectId);

    private void Dismiss()
    {
        State.Dismiss(ProjectId);
        _result = null;
    }

    private static OmniTone Tone(PipelineSetupCollectionOutcome outcome) => outcome switch
    {
        PipelineSetupCollectionOutcome.Done => OmniTone.Success,
        PipelineSetupCollectionOutcome.AlreadyPresent => OmniTone.Neutral,
        _ => OmniTone.Danger
    };

    private string OutcomeText(PipelineSetupCollectionOutcome outcome) => outcome switch
    {
        PipelineSetupCollectionOutcome.Done => L["CollectionSetupDone"],
        PipelineSetupCollectionOutcome.AlreadyPresent => L["CollectionSetupAlreadyPresent"],
        _ => L["CollectionSetupFailed"]
    };

    /// <summary>The item's own name; its value (the site identifier, the origin) or its failure reason is shown next to it.</summary>
    private string ItemLabel(PipelineSetupCollectionKind kind) => kind switch
    {
        PipelineSetupCollectionKind.AppEnabled => L["CollectionSetupAppEnabled"],
        PipelineSetupCollectionKind.IngestKey => L["CollectionSetupIngestKey"],
        PipelineSetupCollectionKind.AudienceEnabled => L["CollectionSetupAudienceEnabled"],
        PipelineSetupCollectionKind.SiteId => L["CollectionSetupSiteId"],
        PipelineSetupCollectionKind.Origins => L["CollectionSetupOrigins"],
        PipelineSetupCollectionKind.PublicIngest => L["CollectionSetupPublicIngest"],
        _ => L["CollectionSetupPseudonymKey"]
    };
}
