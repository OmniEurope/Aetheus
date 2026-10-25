// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineSetupWizardStep
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public int Step { get; set; }
    /// <summary>The "start from" choice, drawn at the top of the Project step.</summary>
    [Parameter] public RenderFragment? StartFrom { get; set; }
    [Parameter] public IReadOnlyList<ProjectDto> Projects { get; set; } = [];
    [Parameter] public int? ProjectId { get; set; }
    [Parameter] public EventCallback<int?> ProjectIdChanged { get; set; }
    [Parameter] public string Prefix { get; set; } = string.Empty;
    [Parameter] public EventCallback<string> PrefixChanged { get; set; }
    /// <summary>The type of what is built and checked, asked before its detail.</summary>
    [Parameter] public PipelineArchitecture? Architecture { get; set; }
    [Parameter] public EventCallback<PipelineArchitecture?> ArchitectureChanged { get; set; }

    /// <summary>The key of the chosen <see cref="PipelineSetupStack"/>.</summary>
    [Parameter] public string Stack { get; set; } = PipelineSetupStack.DotNet.Key;
    [Parameter] public EventCallback<string> StackChanged { get; set; }
    /// <summary>The application shape, null until a tile is chosen: true is the Full-stack tile (one
    /// monolith serving the interface and the API), false the front-end and back-end apart.</summary>
    [Parameter] public bool? SameProject { get; set; }
    [Parameter] public EventCallback<bool?> SameProjectChanged { get; set; }

    /// <summary>The one pipeline an external repository gets.</summary>
    [Parameter] public bool ExternalSecurity { get; set; }
    [Parameter] public EventCallback<bool> ExternalSecurityChanged { get; set; }
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

    /// <summary>The production setup of the summary, set only while the production pipeline is selected (ADR-057).</summary>
    [Parameter] public PipelineSetupProduction? ProductionSetup { get; set; }

    /// <summary>Raised once the project changed from the summary (required libraries and vaults
    /// created, a repository created), so the wizard can re-run its readiness check and drop the
    /// findings that no longer hold.</summary>
    [Parameter] public EventCallback OnReadinessChanged { get; set; }

    /// <summary>The summary with the user's choices, where the pages it opens come back
    /// to.</summary>
    [Parameter] public string? ReturnUrl { get; set; }

    /// <summary>The selected pipelines that would fail at their first run with the templates' default
    /// commands, from what the branch holds (R-10 to R-13); empty when nothing is wrong or nothing was read.</summary>
    [Parameter] public IReadOnlyList<string> DetectionWarnings { get; set; } = [];

    /// <summary>Why the QA option is unticked by default (no test project on the branch); null otherwise.</summary>
    [Parameter] public string? QaNoTestsTip { get; set; }

    private static OmniIconName ArchitectureIcon(PipelineArchitecture architecture) => architecture switch
    {
        PipelineArchitecture.Application => OmniIconName.Monitor,
        PipelineArchitecture.Package => OmniIconName.Package,
        _ => OmniIconName.Link
    };

    private bool Proposes(string capability) => PipelineSetupArchitecture.Proposes(Architecture, capability);

    private IReadOnlyList<OmniOption<string>> StackOptions => PipelineSetupStack.All
        .Select(stack => new OmniOption<string>(stack.Key, L[stack.LabelKey].Value))
        .ToList();

    /// <summary>An option the user switches on or off: an OE selectable card that stands for a check box
    /// (<c>Multiple</c>), identified by the option's name in the wizard's address.</summary>
    private static RenderFragment Option(
        string key,
        OmniIconName icon,
        string title,
        string description,
        bool value,
        EventCallback<bool> changed,
        RenderFragment? note = null) => builder =>
    {
        builder.OpenComponent<OmniSelectableCard>(0);
        builder.SetKey(key);
        builder.AddAttribute(1, nameof(OmniSelectableCard.Multiple), true);
        builder.AddAttribute(2, nameof(OmniSelectableCard.Title), title);
        builder.AddAttribute(3, nameof(OmniSelectableCard.Description), description);
        builder.AddAttribute(4, nameof(OmniSelectableCard.Value), value);
        builder.AddAttribute(5, nameof(OmniSelectableCard.ValueChanged), changed);
        builder.AddAttribute(6, nameof(OmniSelectableCard.Icon), (RenderFragment)(iconBuilder =>
        {
            iconBuilder.OpenComponent<OmniIcon>(0);
            iconBuilder.AddAttribute(1, nameof(OmniIcon.Name), icon);
            iconBuilder.CloseComponent();
        }));
        builder.AddAttribute(7, "data-testid", $"setup-option-{key}");
        if (note is not null)
            builder.AddAttribute(8, nameof(OmniSelectableCard.ChildContent), note);
        builder.CloseComponent();
    };
}

public sealed record PipelineSetupSummaryItem(string Name, string TemplateName);
