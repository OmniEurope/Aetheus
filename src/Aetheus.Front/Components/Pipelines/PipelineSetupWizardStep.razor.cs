// SPDX-License-Identifier: EUPL-1.2

using System.Linq.Expressions;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineSetupWizardStep
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int Step { get; set; }
    [Parameter] public IReadOnlyList<ProjectDto> Projects { get; set; } = [];
    [Parameter] public int? ProjectId { get; set; }
    [Parameter] public EventCallback<int?> ProjectIdChanged { get; set; }
    [Parameter] public string Prefix { get; set; } = string.Empty;
    [Parameter] public EventCallback<string> PrefixChanged { get; set; }
    [Parameter] public bool Frontend { get; set; }
    [Parameter] public EventCallback<bool> FrontendChanged { get; set; }
    [Parameter] public bool Backend { get; set; }
    [Parameter] public EventCallback<bool> BackendChanged { get; set; }
    [Parameter] public bool Database { get; set; }
    [Parameter] public EventCallback<bool> DatabaseChanged { get; set; }
    [Parameter] public bool Ci { get; set; }
    [Parameter] public EventCallback<bool> CiChanged { get; set; }
    [Parameter] public bool Quality { get; set; }
    [Parameter] public EventCallback<bool> QualityChanged { get; set; }
    [Parameter] public bool Security { get; set; }
    [Parameter] public EventCallback<bool> SecurityChanged { get; set; }
    [Parameter] public bool Qa { get; set; }
    [Parameter] public EventCallback<bool> QaChanged { get; set; }
    [Parameter] public bool QaExtended { get; set; }
    [Parameter] public EventCallback<bool> QaExtendedChanged { get; set; }
    [Parameter] public bool SecurityHistory { get; set; }
    [Parameter] public EventCallback<bool> SecurityHistoryChanged { get; set; }
    [Parameter] public bool Candidate { get; set; }
    [Parameter] public EventCallback<bool> CandidateChanged { get; set; }
    [Parameter] public bool Nightly { get; set; }
    [Parameter] public EventCallback<bool> NightlyChanged { get; set; }
    [Parameter] public bool Production { get; set; }
    [Parameter] public EventCallback<bool> ProductionChanged { get; set; }
    [Parameter] public bool FastDeploy { get; set; }
    [Parameter] public EventCallback<bool> FastDeployChanged { get; set; }
    [Parameter] public bool Rollback { get; set; }
    [Parameter] public EventCallback<bool> RollbackChanged { get; set; }
    [Parameter] public bool Packages { get; set; }
    [Parameter] public EventCallback<bool> PackagesChanged { get; set; }
    [Parameter] public string ProjectName { get; set; } = string.Empty;
    [Parameter] public string ArchitectureSummary { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<PipelineSetupSummaryItem> SummaryItems { get; set; } = [];

    /// <summary>Pre-creation readiness for the summary step; null while it has not been loaded.</summary>
    [Parameter] public PipelineSetupReadinessDto? Readiness { get; set; }
    [Parameter] public bool ReadinessLoading { get; set; }

    /// <summary>PLAN-003 lot 30: raised once the required libraries and vaults were created, so the
    /// wizard can re-run its readiness check and drop the findings that no longer hold.</summary>
    [Parameter] public EventCallback OnRequirementsProvisioned { get; set; }

    private static RenderFragment Option(
        OmniIconName icon,
        string title,
        string description,
        bool value,
        EventCallback<bool> changed) => builder =>
    {
        builder.OpenElement(0, "label");
        builder.AddAttribute(1, "class", "pipeline-setup-option");
        builder.OpenComponent<OmniCheckBox<bool>>(2);
        builder.AddAttribute(3, "Value", value);
        builder.AddAttribute(4, "ValueChanged", changed);
        builder.AddAttribute(5, "ValueExpression", (Expression<Func<bool>>)(() => value));
        builder.CloseComponent();
        builder.OpenComponent<OmniIcon>(6);
        builder.AddAttribute(7, "Name", icon);
        builder.CloseComponent();
        builder.OpenElement(8, "span");
        builder.OpenElement(9, "strong");
        builder.AddContent(10, title);
        builder.CloseElement();
        builder.OpenElement(11, "small");
        builder.AddContent(12, description);
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
    };
}

public sealed record PipelineSetupSummaryItem(string Name, string TemplateName);
