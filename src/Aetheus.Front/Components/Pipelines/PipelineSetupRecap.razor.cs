// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>The summary step of the pipeline setup wizard: what will be created, and what is left to do
/// before the pipelines can be created.</summary>
public partial class PipelineSetupRecap
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }
    [Parameter] public string ProjectName { get; set; } = string.Empty;
    [Parameter] public string ArchitectureSummary { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<PipelineSetupSummaryItem> SummaryItems { get; set; } = [];
    [Parameter] public PipelineSetupReadinessDto? Readiness { get; set; }
    [Parameter] public bool ReadinessLoading { get; set; }
    [Parameter] public PipelineSetupProduction? ProductionSetup { get; set; }
    [Parameter] public string? ReturnUrl { get; set; }
    [Parameter] public EventCallback OnReadinessChanged { get; set; }

    private IReadOnlyList<string> TemplateNames => [.. SummaryItems.Select(item => item.TemplateName)];
}
