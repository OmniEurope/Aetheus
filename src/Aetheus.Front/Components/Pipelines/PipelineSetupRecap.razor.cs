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

    /// <summary>R-69: set when the production or QA pipeline is selected but cannot be created from the external repository.</summary>
    [Parameter] public PipelineSetupProduction? ExternalDeployBlocked { get; set; }

    /// <summary>R-69: the application is an external repository, so the repository to prepare is attached, not created.</summary>
    [Parameter] public bool ExternalApplication { get; set; }

    /// <summary>R-20: the optional first push offered once the repository is created; null outside the wizard's own summary.</summary>
    [Parameter] public PipelineSetupFirstPush? FirstPush { get; set; }
    /// <summary>R-60: the deployment pipeline is created, so the summary says whether it asks for an approval before production.</summary>
    [Parameter] public bool ApprovalOffered { get; set; }
    [Parameter] public bool ApprovalAsked { get; set; }
    /// <summary>R-47: the application's telemetry is offered, so the summary says whether it is configured.</summary>
    [Parameter] public bool TelemetryOffered { get; set; }
    [Parameter] public bool TelemetryChosen { get; set; }
    /// <summary>R-26: the application's audience is offered, so the summary says whether it is prepared, and what is.</summary>
    [Parameter] public bool AudienceOffered { get; set; }
    [Parameter] public bool AudienceChosen { get; set; }
    /// <summary>R-17: the repository could not be read to the end, so the languages the quality pipeline keeps are not certain.</summary>
    [Parameter] public bool QualityLanguagesUncertain { get; set; }
    [Parameter] public string? ReturnUrl { get; set; }
    [Parameter] public EventCallback OnReadinessChanged { get; set; }

    private IReadOnlyList<string> ExistingNames => [.. SummaryItems.Where(item => item.Exists).Select(item => item.Name)];

    /// <summary>R-37: the templates the readiness check read, when they are more than the pipelines listed (the production
    /// deployment's, for the vault a fast release alone prepares); the creation of the libraries and vaults reads the same list.</summary>
    [Parameter] public IReadOnlyList<string>? ReadinessTemplateNames { get; set; }

    private IReadOnlyList<string> TemplateNames => ReadinessTemplateNames ?? [.. SummaryItems.Select(item => item.TemplateName)];
}
