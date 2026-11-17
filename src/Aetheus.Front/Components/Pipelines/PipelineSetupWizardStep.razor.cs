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
    /// <summary>The branch every pipeline created reads (R-94), and the repository's branches to choose it from.</summary>
    [Parameter] public string? SourceBranch { get; set; }
    [Parameter] public EventCallback<string> SourceBranchChanged { get; set; }
    [Parameter] public IReadOnlyList<OmniOption<string>> SourceBranches { get; set; } = [];
    /// <summary>True once the project's branch was looked for and neither the project nor its repository names one (R-76): the
    /// pipelines would be written without <c>source_branch:</c>, which the step says.</summary>
    [Parameter] public bool SourceBranchMissing { get; set; }
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

    /// <summary>R-12: the minimal security of an external repository, exclusive with <see cref="ExternalSecurityComplete"/>.</summary>
    [Parameter] public bool ExternalSecurity { get; set; }
    [Parameter] public EventCallback<bool> ExternalSecurityChanged { get; set; }
    [Parameter] public bool ExternalSecurityComplete { get; set; }
    [Parameter] public EventCallback<bool> ExternalSecurityCompleteChanged { get; set; }

    /// <summary>R-34, R-12: the complete quality evidence of an external repository, offered when ExternalQualityOffered, exclusive
    /// with <see cref="ExternalQualityMinimal"/>.</summary>
    [Parameter] public bool ExternalQuality { get; set; }
    [Parameter] public EventCallback<bool> ExternalQualityChanged { get; set; }
    [Parameter] public bool ExternalQualityMinimal { get; set; }
    [Parameter] public EventCallback<bool> ExternalQualityMinimalChanged { get; set; }
    [Parameter] public bool ExternalQualityOffered { get; set; }
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
    /// <summary>R-105: the languages the quality pipeline analyses, one tile each.</summary>
    [Parameter] public bool LangDotNet { get; set; }
    [Parameter] public EventCallback<bool> LangDotNetChanged { get; set; }
    [Parameter] public bool LangPython { get; set; }
    [Parameter] public EventCallback<bool> LangPythonChanged { get; set; }
    [Parameter] public bool LangJava { get; set; }
    [Parameter] public EventCallback<bool> LangJavaChanged { get; set; }
    /// <summary>R-72: the candidate starts the deployment pipeline created with it. Offered only when both are created.</summary>
    [Parameter] public bool DeployChain { get; set; } = true;
    [Parameter] public EventCallback<bool> DeployChainChanged { get; set; }
    [Parameter] public bool DeployChainOffered { get; set; }
    /// <summary>R-60: the deployment pipeline asks for an approval before production, even for a release graded A. Offered
    /// only when the deployment pipeline is created, ticked by default.</summary>
    [Parameter] public bool DeployApproval { get; set; } = true;
    [Parameter] public EventCallback<bool> DeployApprovalChanged { get; set; }
    [Parameter] public bool DeployApprovalOffered { get; set; }
    /// <summary>R-47: the telemetry of the application is configured when the pipelines are created. Offered for an
    /// application, ticked by default.</summary>
    [Parameter] public bool Telemetry { get; set; } = true;
    [Parameter] public EventCallback<bool> TelemetryChanged { get; set; }
    [Parameter] public bool TelemetryOffered { get; set; }
    /// <summary>PLAN-007 (R-9): the project's automatic dependency updates, offered for an application and a package (never for
    /// an external repository, whose mirror is never written), ticked by default.</summary>
    [Parameter] public bool DependencyUpdates { get; set; } = true;
    [Parameter] public EventCallback<bool> DependencyUpdatesChanged { get; set; }
    [Parameter] public bool DependencyUpdatesOffered { get; set; }
    /// <summary>R-26: the audience (visitors) of the application is prepared when the pipelines are created. Offered for an
    /// application; ticked by default when it has a web front.</summary>
    [Parameter] public bool Audience { get; set; } = true;
    [Parameter] public EventCallback<bool> AudienceChanged { get; set; }
    [Parameter] public bool AudienceOffered { get; set; }
    /// <summary>The repository could not be read to the end for one of the languages: the user confirms which to keep.</summary>
    [Parameter] public bool LanguagesUncertain { get; set; }

    /// <summary>R-13: the expand/contract model of the database, which turns the project's migration gate on once the pipelines exist.
    /// Offered for an application that declares a database, unticked by default.</summary>
    [Parameter] public bool ExpandContract { get; set; }
    [Parameter] public EventCallback<bool> ExpandContractChanged { get; set; }
    [Parameter] public bool ExpandContractOffered { get; set; }

    /// <summary>R-105: a quality pipeline is created (the languages are asked for it and for nothing else), whichever kind.</summary>
    private bool QualityLanguagesShown =>
        (Proposes("quality") && Quality)
        || (ExternalQualityOffered && ((Proposes("external-quality") && ExternalQuality) || (Proposes("external-quality-minimal") && ExternalQualityMinimal)));

    /// <summary>R-17: told in the summary, and only when a quality pipeline is created (the languages concern nothing else).</summary>
    private bool QualityLanguagesUncertain => LanguagesUncertain && QualityLanguagesShown;
    [Parameter] public string ProjectName { get; set; } = string.Empty;
    [Parameter] public string ArchitectureSummary { get; set; } = string.Empty;
    [Parameter] public IReadOnlyList<PipelineSetupSummaryItem> SummaryItems { get; set; } = [];

    /// <summary>Pre-creation readiness for the summary step; null while it has not been loaded.</summary>
    [Parameter] public PipelineSetupReadinessDto? Readiness { get; set; }
    [Parameter] public bool ReadinessLoading { get; set; }

    /// <summary>R-37: the templates the readiness check read, which the summary creates the libraries and vaults of; null when they
    /// are the summary's pipelines' own.</summary>
    [Parameter] public IReadOnlyList<string>? ReadinessTemplateNames { get; set; }

    /// <summary>The production setup of the summary, set while the production pipeline or (R-37) the fast release is selected (ADR-057).</summary>
    [Parameter] public PipelineSetupProduction? ProductionSetup { get; set; }

    /// <summary>R-69: the production setup, set only while the production or QA pipeline is selected for a project whose
    /// external source gave no Compose file: those pipelines are not created and the summary says why.</summary>
    [Parameter] public PipelineSetupProduction? ExternalDeployBlocked { get; set; }

    /// <summary>R-69: the application is an external repository, attached rather than created.</summary>
    [Parameter] public bool ExternalApplication { get; set; }

    /// <summary>R-20: the optional first push offered once the repository is created; null outside the wizard's own summary.</summary>
    [Parameter] public PipelineSetupFirstPush? FirstPush { get; set; }

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

    /// <summary>R-19: the production strategy, the replacement of the services until the user picks blue-green.</summary>
    [Parameter] public string Strategy { get; set; } = ProductionStrategy.Replace;
    [Parameter] public EventCallback<string> StrategyChanged { get; set; }

    /// <summary>R-11: a monolith is deployed by replacement only, so the strategy tile shows that and offers no other.</summary>
    [Parameter] public bool StrategyFixed { get; set; }

    /// <summary>R-19: the strategy is asked once a pipeline that deploys to production is selected.</summary>
    private bool StrategyOffered => Proposes("production") && (Production || FastDeploy);

    /// <summary>R-26: the strategy tiles are drawn after the grid of the category that holds them, as two exclusive tiles.</summary>
    private bool StrategyShown(PipelineSetupCategory category) =>
        StrategyOffered && category.Capabilities.Contains(PipelineSetupCategories.ProductionStrategy);

    /// <summary>R-23: the project already holds its production deployment pipeline, which the wizard keeps as it is: the tiles that
    /// need a production deployment then have nothing to wait for.</summary>
    [Parameter] public bool ProductionHeld { get; set; }

    /// <summary>R-23: whether the capability a tile depends on is ticked. A candidate also exists when the nightly is selected (the
    /// wizard creates it for the nightly), a production deployment when the project already holds it.</summary>
    private bool Met(string dependency) => dependency switch
    {
        "nightly" => Nightly,
        "candidate" => Candidate || Nightly,
        "production" => Production || ProductionHeld,
        _ => true
    };

    /// <summary>R-23: the capabilities <paramref name="key"/> depends on that are not ticked.</summary>
    private IReadOnlyList<string> Unmet(string key) => [.. PipelineSetupCapabilityDependencies.Of(key).Where(dependency => !Met(dependency))];

    /// <summary>R-23: the mention "depends on ..." of a greyed tile, null when every dependency is ticked.</summary>
    private RenderFragment? DependencyNote(string key)
    {
        var unmet = Unmet(key);
        if (unmet.Count == 0) return null;
        var names = string.Join(", ", unmet.Select(dependency => L[PipelineSetupCapabilityDependencies.TitleKey(dependency)].Value));
        return builder =>
        {
            builder.OpenComponent<OmniBadge>(0);
            builder.AddAttribute(1, nameof(OmniBadge.Icon), BadgeIcon.For(OmniTone.Neutral));
            builder.AddAttribute(2, nameof(OmniBadge.Tone), OmniTone.Neutral);
            builder.AddAttribute(3, nameof(OmniBadge.Fill), OmniFill.Tonal);
            builder.AddAttribute(4, "data-testid", "setup-dependency-note");
            builder.AddAttribute(5, "data-depends-on", string.Join(' ', unmet));
            builder.AddAttribute(6, nameof(OmniBadge.ChildContent), (RenderFragment)(content =>
                content.AddContent(0, L["PipelineSetupDependsOn", names].Value)));
            builder.CloseComponent();
        };
    }

    /// <summary>The tile of one capability of <see cref="PipelineSetupCategories"/>, or null when this type, branch or state does
    /// not offer it (the category is then drawn without it, or not at all).</summary>
    private RenderFragment? Tile(string key, RenderFragment? qaNote) =>
        ExternalTile(key) ?? VerificationTile(key, qaNote) ?? DeliveryTile(key) ?? ProjectOptionTile(key);

    // R-12: security and quality of an external repository are each two exclusive tiles, the minimal and the complete one.
    private RenderFragment? ExternalTile(string key) => key switch
    {
        PipelineSetupExternalOptions.Security => Offer(key, Proposes(key), OmniIconName.ShieldCheck, L["ExternalSecurityPipeline"], L["ExternalSecurityPipelineHint"], L["ExternalSecurityPipelineHelp"], ExternalSecurity, ExternalSecurityChanged),
        PipelineSetupExternalOptions.SecurityComplete => Offer(key, Proposes(key), OmniIconName.ShieldCheck, L["ExternalSecurityCompletePipeline"], L["ExternalSecurityCompletePipelineHint"], L["ExternalSecurityCompletePipelineHelp"], ExternalSecurityComplete, ExternalSecurityCompleteChanged),
        PipelineSetupExternalOptions.QualityMinimal => Offer(key, Proposes(key) && ExternalQualityOffered, OmniIconName.ChartLine, L["ExternalQualityMinimalPipeline"], L["ExternalQualityMinimalPipelineHint"], L["ExternalQualityMinimalPipelineHelp"], ExternalQualityMinimal, ExternalQualityMinimalChanged),
        PipelineSetupExternalOptions.Quality => Offer(key, Proposes(key) && ExternalQualityOffered, OmniIconName.ChartLine, L["ExternalQualityPipeline"], L["ExternalQualityPipelineHint"], L["ExternalQualityPipelineHelp"], ExternalQuality, ExternalQualityChanged),
        _ => null
    };

    private RenderFragment? VerificationTile(string key, RenderFragment? qaNote) => key switch
    {
        "ci" => Offer(key, Proposes(key), OmniIconName.Wrench, "CI", L["CiPipelineHint"], L["CiPipelineHelp"], Ci, CiChanged),
        "quality" => Offer(key, Proposes(key), OmniIconName.ChartLine, L["Quality"], L["QualityPipelineHint"], L["QualityPipelineHelp"], Quality, QualityChanged),
        "security" => Offer(key, Proposes(key), OmniIconName.ShieldCheck, L["Security"], L["SecurityPipelineHint"], L["SecurityPipelineHelp"], Security, SecurityChanged),
        "qa" => Offer(key, Proposes(key), OmniIconName.Flask, "QA", L["QaPipelineHint"], L["QaPipelineHelp"], Qa, QaChanged, qaNote),
        // R-23: only the nightly starts the extended QA and the secret history, so both wait for it.
        "qa-extended" => Offer(key, Proposes(key), OmniIconName.Gauge, L["QaExtended"], L["QaExtendedPipelineHint"], L["QaExtendedPipelineHelp"], QaExtended, QaExtendedChanged, DependencyNote(key), IsWaiting(key, QaExtended)),
        "security-history" => Offer(key, Proposes(key), OmniIconName.Key, L["SecurityHistory"], L["SecurityHistoryPipelineHint"], L["SecurityHistoryPipelineHelp"], SecurityHistory, SecurityHistoryChanged, DependencyNote(key), IsWaiting(key, SecurityHistory)),
        _ => null
    };

    private RenderFragment? DeliveryTile(string key) => key switch
    {
        "candidate" => Offer(key, Proposes(key), OmniIconName.CheckCircle, L["Candidate"], L["CandidatePipelineHint"], L["CandidatePipelineHelp"], Candidate, CandidateChanged),
        "nightly" => Offer(key, Proposes(key), OmniIconName.Timer, L["Nightly"], L["NightlyPipelineHint"], L["NightlyPipelineHelp"], Nightly, NightlyChanged),
        "production" => Offer(key, Proposes(key), OmniIconName.RocketLaunch, L["ProductionDeployment"], L["ProductionPipelineHint"], L["ProductionPipelineHelp"], Production, ProductionChanged),
        "fast-deploy" => Offer(key, Proposes(key), OmniIconName.Lightning, L["FastDeploy"], L["FastPipelineHint"], L["FastPipelineHelp"], FastDeploy, FastDeployChanged),
        "rollback" => Offer(key, Proposes(key), OmniIconName.Reset, L["Rollback"], L["RollbackPipelineHint"], L["RollbackPipelineHelp"], Rollback, RollbackChanged),
        "packages" => Offer(key, Proposes(key), OmniIconName.Package, L["Packages"], L["PackagesPipelineHint"], L["PackagesPipelineHelp"], Packages, PackagesChanged),
        _ => null
    };

    private RenderFragment? ProjectOptionTile(string key) => key switch
    {
        // R-72: the candidate starts the deployment pipeline created with it, unless the user unticks it. Offered only when both are
        // created; while the candidate or the production deployment is unticked it is drawn greyed with what it depends on (R-23).
        "deploy-after-candidate" => Waiting(key, DeployChainOffered, OmniIconName.RocketLaunch, L["PipelineSetupRunDeployAfterCandidate"], L["PipelineSetupRunDeployAfterCandidateHint"], L["PipelineSetupRunDeployAfterCandidateHelp"], DeployChain, DeployChainChanged),
        // R-60: a production deployment asks for an approval by default, even for a release graded A; the user may untick it.
        "deploy-approval" => Waiting(key, DeployApprovalOffered, OmniIconName.ShieldCheck, L["PipelineSetupAskApprovalBeforeProduction"], L["PipelineSetupAskApprovalBeforeProductionHint"], L["PipelineSetupAskApprovalBeforeProductionHelp"], DeployApproval, DeployApprovalChanged),
        // R-13: the expand/contract model of the database, which turns the project's migration gate on. Unticked by default.
        "expand-contract" => Offer(key, ExpandContractOffered, OmniIconName.Database, L["PipelineSetupExpandContract"], L["PipelineSetupExpandContractHint"], L["PipelineSetupExpandContractHelp"], ExpandContract, ExpandContractChanged),
        // PLAN-007 (R-9): the project's automatic dependency updates, ticked by default; not for an external repository.
        "dependency-updates" => Offer(key, DependencyUpdatesOffered, OmniIconName.Package, L["PipelineSetupDependencyUpdates"], L["PipelineSetupDependencyUpdatesHint"], L["PipelineSetupDependencyUpdatesHelp"], DependencyUpdates, DependencyUpdatesChanged),
        // R-47, R-26: what the user chooses to collect (the technical telemetry, the visitors) is prepared automatically for the
        // project's monitored application when the pipelines are created.
        "telemetry" => Offer(key, TelemetryOffered, OmniIconName.Heartbeat, L["PipelineSetupTelemetry"], L["PipelineSetupTelemetryHint"], L["PipelineSetupTelemetryHelp"], Telemetry, TelemetryChanged),
        "audience" => Offer(key, AudienceOffered, OmniIconName.Users, L["PipelineSetupAudience"], L["PipelineSetupAudienceHint"], L["PipelineSetupAudienceHelp"], Audience, AudienceChanged),
        _ => null
    };

    private RenderFragment? Offer(
        string key, bool offered, OmniIconName icon, string title, string description, string help, bool value, EventCallback<bool> changed,
        RenderFragment? note = null, bool disabled = false) =>
        offered ? Capability(key, icon, title, description, help, value, changed, note, disabled) : null;

    /// <summary>R-23: a ticked tile whose dependency is not ticked (a template asked for it alone) stays usable, so it can be
    /// unticked; an unticked one is greyed.</summary>
    private bool IsWaiting(string key, bool value) => !value && Unmet(key).Count > 0;

    /// <summary>R-23: a tile the wizard offers only once its dependencies are ticked (<paramref name="offered"/>): while they are not,
    /// and the type proposes the deployment it belongs to, it is drawn greyed and unticked with the mention of what it depends on,
    /// never ticked on the user's behalf; ticking the dependency offers it again with its own value.</summary>
    private RenderFragment? Waiting(
        string key, bool offered, OmniIconName icon, string title, string description, string help, bool value, EventCallback<bool> changed)
    {
        if (offered) return Capability(key, icon, title, description, help, value, changed);
        var waiting = Proposes("production") && Proposes("candidate") && Unmet(key).Count > 0;
        return waiting ? Capability(key, icon, title, description, help, false, changed, DependencyNote(key), disabled: true) : null;
    }

    private IReadOnlyList<OmniOption<string>> StackOptions => PipelineSetupStack.All
        .Select(stack => new OmniOption<string>(stack.Key, L[stack.LabelKey].Value))
        .ToList();

    /// <summary>A capability the user switches on or off: an OE selectable card that stands for a check box (<c>Multiple</c>), with
    /// the "?" that explains it (R-22), identified by the capability's name in the wizard's address.</summary>
    private static RenderFragment Capability(
        string key,
        OmniIconName icon,
        string title,
        string description,
        string help,
        bool value,
        EventCallback<bool> changed,
        RenderFragment? note = null,
        bool disabled = false) => builder =>
    {
        builder.OpenComponent<PipelineSetupCapabilityTile>(0);
        builder.SetKey(key);
        builder.AddAttribute(1, nameof(PipelineSetupCapabilityTile.Key), key);
        builder.AddAttribute(2, nameof(PipelineSetupCapabilityTile.Icon), icon);
        builder.AddAttribute(3, nameof(PipelineSetupCapabilityTile.Title), title);
        builder.AddAttribute(4, nameof(PipelineSetupCapabilityTile.Description), description);
        builder.AddAttribute(5, nameof(PipelineSetupCapabilityTile.Help), help);
        builder.AddAttribute(6, nameof(PipelineSetupCapabilityTile.Value), value);
        builder.AddAttribute(7, nameof(PipelineSetupCapabilityTile.ValueChanged), changed);
        builder.AddAttribute(8, nameof(PipelineSetupCapabilityTile.Disabled), disabled);
        // Always written, even null: a parameter that is not supplied again keeps its previous value, and a tile that is no longer
        // greyed must lose its mention.
        builder.AddAttribute(9, nameof(PipelineSetupCapabilityTile.Note), note);
        builder.CloseComponent();
    };

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

/// <param name="Exists">R-38: the project already holds a pipeline of this name; the wizard keeps it and creates nothing.</param>
public sealed record PipelineSetupSummaryItem(string Name, string TemplateName, bool Exists = false);
