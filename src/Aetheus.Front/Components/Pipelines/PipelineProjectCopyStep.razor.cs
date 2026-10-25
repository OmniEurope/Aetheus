// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>One step of <see cref="PipelineProjectCopyFlow"/> : the projects, the pipelines to
/// take over with their verdict, then the summary and, once copied, what remains to set
/// up.</summary>
public partial class PipelineProjectCopyStep
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int Step { get; set; }
    [Parameter, EditorRequired] public IPipelineProjectCopyFlow Flow { get; set; } = default!;
    /// <summary>The "start from" choice, drawn at the top of the Project step.</summary>
    [Parameter] public RenderFragment? StartFrom { get; set; }
    [Parameter] public EventCallback OnReadinessChanged { get; set; }

    private static OmniTone StatusTone(PipelineCopyStatus status) => status switch
    {
        PipelineCopyStatus.Ready => OmniTone.Success,
        PipelineCopyStatus.ToReview => OmniTone.Warning,
        _ => OmniTone.Danger
    };
}
