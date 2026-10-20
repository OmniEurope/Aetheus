// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>One step of <see cref="PipelineProjectCopyWizard"/> : the projects, the pipelines to
/// take over with their verdict, then the summary and, once copied, what remains to set
/// up.</summary>
public partial class PipelineProjectCopyStep
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int Step { get; set; }
    [Parameter, EditorRequired] public PipelineProjectCopyWizard Flow { get; set; } = default!;
    [Parameter] public EventCallback OnReadinessChanged { get; set; }

    private static OmniTone StatusTone(PipelineCopyStatus status) => status switch
    {
        PipelineCopyStatus.Ready => OmniTone.Success,
        PipelineCopyStatus.ToReview => OmniTone.Warning,
        _ => OmniTone.Danger
    };
}
