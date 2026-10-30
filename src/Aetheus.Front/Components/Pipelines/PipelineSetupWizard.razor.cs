// SPDX-License-Identifier: EUPL-1.2

using Microsoft.AspNetCore.Components.Rendering;

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineSetupWizard
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;

    [SupplyParameterFromQuery] public int? ProjectId { get; set; }
    [SupplyParameterFromQuery] public int? TemplateId { get; set; }

    // The summary with its choices travels in its own address (prefix, chosen options, step), so a
    // page opened from it to fix something comes back to the same summary. Nothing here is
    // sensitive: a pipeline name prefix and option names.
    [SupplyParameterFromQuery(Name = "prefix")] public string? PrefixQuery { get; set; }
    [SupplyParameterFromQuery(Name = "options")] public string? OptionsQuery { get; set; }
    [SupplyParameterFromQuery(Name = "step")] public string? StepQuery { get; set; }
    [SupplyParameterFromQuery(Name = "architecture")] public string? ArchitectureQuery { get; set; }
    [SupplyParameterFromQuery(Name = "sameProject")] public string? SameProjectQuery { get; set; }
    [SupplyParameterFromQuery(Name = "branch")] public string? BranchQuery { get; set; }

    [Inject] private PipelineSetupProvisionState ProvisionState { get; set; } = default!;

    private const string SummaryStepSlug = "summary";
    private int _initialStep;

    private readonly List<ProjectDto> _projects = [];
    private readonly List<PipelineTemplateSummaryDto> _templates = [];
    private List<WizardStep> _steps = [];
    private int _currentStep;
    private bool _loading = true;
    private bool _saving;
    private int? _selectedProjectId;
    private string _prefix = string.Empty;

    /// <summary>The application shape is explicitly selected, with no default choice.</summary>
    private PipelineArchitecture? _architecture;
    /// <summary>The application shape, a tri-state choice with no default tile: true is the Full-stack
    /// tile (one monolith serving the interface and the API), false the front-end and back-end apart.
    /// The address keeps its <c>sameProject=yes|no</c> key.</summary>
    private bool? _sameProject;
    private PipelineSetupStack _stack = PipelineSetupStack.DotNet;
    private bool _frontend = true;
    private bool _backend = true;
    private bool _database = true;
    private bool _ci = true;
    private bool _quality = true;
    private bool _security = true;
    private bool _qa = true;
    private bool _qaExtended;
    private bool _securityHistory;
    private bool _candidate = true;
    private bool _nightly = true;
    private bool _production = true;
    private bool _fastDeploy;
    private bool _rollback = true;
    private bool _packages;
    private bool _externalSecurity = true;
    private readonly PipelineSetupDeployChain _deployChain = new();

    /// <summary>Index of the Capabilities step, where what the branch holds is read.</summary>
    private const int CapabilitiesStep = 2;

    /// <summary>What the branch files and the fleet say about the templates' default commands (R-10 to R-13),
    /// read once per project when the Capabilities step opens; null while unknown, never a guess.</summary>
    private PipelineSetupDetectionDto? _detection;
    private string? _detectionBranch;
    private int? _detectionProjectId;

    /// <summary>True once the user (or the address he came back with) decided the QA option himself: the
    /// detection then no longer sets it.</summary>
    private bool _qaTouched;

    /// <summary>R-105: the languages the quality pipeline analyses. They start from the stack (.NET) and, once the repository is
    /// read, from the languages it holds a file of, until the user (or the address he came back with) chose them himself.</summary>
    private PipelineSetupLanguages _languages = PipelineSetupLanguages.Proposal(null, PipelineSetupStack.DotNet);
    private bool _languagesTouched;

    /// <summary>Index of the summary step, where the readiness check runs.</summary>
    private const int SummaryStep = 3;
    private PipelineSetupReadinessDto? _readiness;
    private bool _readinessLoading;
    private string? _readinessSignature;

    /// <summary>The production setup read from the project's Compose file (ADR-057), created on first use; what the creation
    /// of the production pipeline writes into its YAML once it was applied.</summary>
    private PipelineSetupProduction? _productionSetup;
    private PipelineSetupProductionInput? _productionInput;
    private string _productionPending = string.Empty;

    /// <summary>The production pipeline was created without the Compose file, variables, ports and files its first run
    /// needs, because the repository could not be read: the end of the wizard says so (recette R-80).</summary>
    private bool _productionNotPrepared;
    private PipelineSetupProduction ProductionSetup => _productionSetup ??= new PipelineSetupProduction(Api, RefreshSteps);

    /// <summary>The branch every pipeline created reads (recette R-94): develop when the repository has it.</summary>
    private PipelineSetupSourceBranch? _branchChoice;
    private PipelineSetupSourceBranch BranchChoice => _branchChoice ??= new PipelineSetupSourceBranch(Api, RefreshSteps);

    /// <summary>The flow of copying the pipelines of an existing project, set while that "start from"
    /// choice is the one made; the steps, the gate and the finish are then its own.</summary>
    private PipelineProjectCopyFlow? _copy;

    /// <summary>The "start from" choice, the first thing of the Project step of either mode: one click
    /// swaps the steps behind it, and it is no longer drawn once Next went past that step.</summary>
    private RenderFragment StartFromContent => builder =>
    {
        builder.OpenComponent<PipelineSetupStartFrom>(0);
        builder.AddAttribute(1, nameof(PipelineSetupStartFrom.CopyMode), _copy is not null);
        builder.AddAttribute(2, nameof(PipelineSetupStartFrom.CopyModeChanged),
            EventCallback.Factory.Create<bool>(this, OnModeChanged));
        builder.CloseComponent();
    };

    /// <summary>Switching the mode keeps the project already chosen, in either direction.</summary>
    private void OnModeChanged(bool copyMode)
    {
        if (copyMode == (_copy is not null)) return;
        if (copyMode)
            _copy = new PipelineProjectCopyFlow(Api, Nav, L, Toast, _projects, _selectedProjectId, RefreshSteps);
        else
        {
            var projectId = _copy?.TargetProjectId;
            _copy = null;
            if (projectId is not null && projectId != _selectedProjectId)
                OnProjectChanged(projectId);
        }
        RefreshSteps();
    }

    private string BackHref => _selectedProjectId is { } id ? $"/projects/{id}/pipelines" : "/pipelines";
    private string FinishText => _copy?.FinishText ?? L["CreatePipelines"].Value;
    private ProjectDto? SelectedProject => _projects.FirstOrDefault(project => project.Id == _selectedProjectId);
    private bool CanAdvance => _copy is { } copy ? copy.CanAdvance(_currentStep) : !_saving && _currentStep switch
    {
        0 => _selectedProjectId.HasValue && !string.IsNullOrWhiteSpace(_prefix),
        1 => PipelineSetupArchitecture.IsComplete(_architecture, _sameProject, _frontend, _backend),
        2 => SelectedRoles.Count > 0,
        // The summary waits for the repository and the libraries and vaults to exist (R2-17), never for
        // the user to open each one of them (R2-19).
        // Nothing else blocks the creation: the wizard asks for no command (R2-25).
        _ => !PipelineSetupCreationGate.IsBlocked(_readiness)
    };

    private IReadOnlyCollection<string> CreatedSuffixes => [.. SelectedRoles.Select(role => role.Suffix)];

    /// <summary>An option counts only when it is chosen AND the type proposes it: a choice made under
    /// another type and hidden since creates nothing and writes nothing.</summary>
    private bool On(string capability, bool value) =>
        value && PipelineSetupArchitecture.Proposes(_architecture, capability);

    private IReadOnlyList<PipelineRole> SelectedRoles
    {
        get
        {
            var roles = new List<PipelineRole>();
            var requiresCi = On("ci", _ci) || On("quality", _quality) || On("security", _security)
                || On("qa", _qa) || On("qa-extended", _qaExtended) || On("security-history", _securityHistory)
                || On("candidate", _candidate) || On("nightly", _nightly);
            Add(requiresCi, "ci", "application-ci");
            Add(On("quality", _quality), "quality", "application-quality");
            Add(On("security", _security), "security", "application-security");
            Add(On("qa", _qa), "qa", "application-qa");
            Add(On("security-history", _securityHistory), "security-history", "application-security-history");
            Add(On("qa-extended", _qaExtended), "qa-extended", "application-qa-extended");
            Add(On("candidate", _candidate) || On("nightly", _nightly), "candidate", "application-candidate");
            Add(On("nightly", _nightly), "nightly", "application-nightly");
            Add(On("production", _production), PipelineSetupRoles.DeployProd, PipelineSetupRoles.DeployProdTemplate);
            Add(On("fast-deploy", _fastDeploy), "release-fast", PipelineSetupRoles.ReleaseFastTemplate);
            if (On("packages", _packages))
            {
                Add(true, "package-telemetry", "package-telemetry");
                Add(true, _stack.AnalyticsPackageTemplate, _stack.AnalyticsPackageTemplate);
                Add(true, "package-web-analytics-browser", "package-web-analytics-browser");
                Add(true, "publish-observability-packages", "publish-observability-packages");
            }
            Add(On("external-security", _externalSecurity), "security", "application-security-external");
            return roles;

            void Add(bool enabled, string suffix, string templateName)
            {
                if (enabled) roles.Add(new PipelineRole(suffix, templateName));
            }
        }
    }

    protected override async Task OnInitializedAsync()
    {
        Breadcrumb.Set(
            new BreadcrumbItem(L["Pipelines"], "/pipelines"),
            new BreadcrumbItem(L["PipelineSetupWizard"]));
        try
        {
            var projectsTask = Api.Projects.GetProjectsAsync(1, 200);
            var templatesTask = Api.PipelineTemplates.GetPipelineTemplatesAsync();
            await Task.WhenAll(projectsTask, templatesTask);
            _projects.AddRange((await projectsTask).Items);
            _templates.AddRange(await templatesTask);
            _selectedProjectId = ProjectId;
            BranchChoice.Follow(SelectedProject);
            SetPrefixFromProject();
            if (OptionsQuery is not null)
                RestoreChoices();
            else if (TemplateId is { } templateId)
                SelectOnlyRequestedTemplate(templateId);
        }
        catch (HttpRequestException)
        {
            Toast.Error(L["LoadFailed"]);
        }
        RefreshSteps();
        _loading = false;
        if (_initialStep == SummaryStep) await LoadSummaryAsync();
    }

    /// <summary>The wizard's options, by the name they carry in the summary's address.</summary>
    private (string Name, Func<bool> Get, Action<bool> Set)[] Choices =>
    [
        ("frontend", () => _frontend, value => _frontend = value),
        ("backend", () => _backend, value => _backend = value),
        ("database", () => _database, value => _database = value),
        ("ci", () => _ci, value => _ci = value),
        ("quality", () => _quality, value => _quality = value),
        ("security", () => _security, value => _security = value),
        ("qa", () => _qa, value => _qa = value),
        ("qa-extended", () => _qaExtended, value => _qaExtended = value),
        ("security-history", () => _securityHistory, value => _securityHistory = value),
        ("candidate", () => _candidate, value => _candidate = value),
        ("nightly", () => _nightly, value => _nightly = value),
        ("production", () => _production, value => _production = value),
        ("fast-deploy", () => _fastDeploy, value => _fastDeploy = value),
        ("rollback", () => _rollback, value => _rollback = value),
        ("packages", () => _packages, value => _packages = value),
        ("external-security", () => _externalSecurity, value => _externalSecurity = value),
        _deployChain.Choice,
        (PipelineSetupLanguages.DotNetOption, () => _languages.DotNet, value => _languages = _languages with { DotNet = value }),
        (PipelineSetupLanguages.PythonOption, () => _languages.Python, value => _languages = _languages with { Python = value }),
        (PipelineSetupLanguages.JavaOption, () => _languages.Java, value => _languages = _languages with { Java = value }),
        (PipelineSetupLanguages.NoneOption, () => _languages is { DotNet: false, Python: false, Java: false }, _ => { })
    ];

    /// <summary> The address of this summary with the user's choices. The pages the summary opens
    /// to fix something come back to it, and it replaces the wizard's own entry in the history, so
    /// the browser's Back lands on the same summary too. </summary>
    private string? SummaryUrl => _selectedProjectId is { } projectId
        ? PipelineSetupAddress.Summary(projectId, _prefix,
            Choices.Where(choice => choice.Get() && InAddress(choice.Name)).Select(choice => choice.Name),
            _architecture, _sameProject, BranchChoice.Picked, SummaryStepSlug)
        : null;

    /// <summary>The address names the parts of an application and what the type proposes, not an
    /// option the type hides.</summary>
    private bool InAddress(string name) =>
        name is "frontend" or "backend" or "database" || PipelineSetupArchitecture.Proposes(_architecture, name)
        || (name.StartsWith("lang-", StringComparison.Ordinal) && On("quality", _quality))
        || (name == PipelineSetupDeployChain.SkipOption && PipelineSetupDeployChain.IsOffered(CreatedSuffixes));

    /// <summary>Takes back the choices the summary's address carries; an option it does not name is off.
    /// The summary is reopened only for a project and a selection that can reach it.</summary>
    private void RestoreChoices()
    {
        var chosen = OptionsQuery!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // An address written before R-105 names no language: the proposal stands instead of unticking all three.
        _languagesTouched = chosen.Any(name => name.StartsWith("lang-", StringComparison.Ordinal));
        foreach (var choice in Choices.Where(choice => _languagesTouched || !choice.Name.StartsWith("lang-", StringComparison.Ordinal)))
            choice.Set(chosen.Contains(choice.Name));
        _qaTouched = true;
        if (!string.IsNullOrWhiteSpace(PrefixQuery))
            _prefix = PrefixQuery.Trim();
        // An address written before the type existed named the parts only: those were the separate
        // frontend and backend options, so it reopens as an application whose interface is not served by
        // the server project, with the same pipelines.
        var legacy = ArchitectureQuery is null && (_frontend || _backend || _database);
        _architecture = PipelineSetupArchitecture.FromSlug(ArchitectureQuery)
            ?? (legacy ? PipelineArchitecture.Application : null);
        _sameProject = legacy ? false : PipelineSetupArchitecture.ParseSameProject(SameProjectQuery);
        if (BranchQuery is not null) BranchChoice.Pick(BranchQuery);
        if (CanReopenSummary())
        {
            _initialStep = SummaryStep;
            _currentStep = SummaryStep;
        }
    }

    private bool CanReopenSummary() =>
        string.Equals(StepQuery, SummaryStepSlug, StringComparison.OrdinalIgnoreCase)
        && SelectedProject is not null && !string.IsNullOrWhiteSpace(_prefix)
        && PipelineSetupArchitecture.IsComplete(_architecture, _sameProject, _frontend, _backend) && SelectedRoles.Count > 0;

    private void RefreshSteps()
    {
        _steps = _copy?.Steps(StartFromContent) ??
        [
            new() { Title = L["Project"], Content = RenderStep(0) },
            new() { Title = L["PipelineArchitectureTitle"], Content = RenderStep(1) },
            new() { Title = L["WizardStepCapabilities"], Content = RenderStep(2) },
            new() { Title = L["WizardStepSummary"], Content = RenderStep(3) }
        ];
        StateHasChanged();
    }

    private RenderFragment RenderStep(int step) => builder =>
    {
        builder.OpenComponent<PipelineSetupWizardStep>(0);
        builder.AddAttribute(1, nameof(PipelineSetupWizardStep.Step), step);
        builder.AddAttribute(7, nameof(PipelineSetupWizardStep.StartFrom), StartFromContent);
        AddProjectAttributes(builder);
        AddPipelineToggles(builder);
        AddOptionToggles(builder);
        AddSummaryAttributes(builder);
        AddArchitectureAttributes(builder);
        builder.CloseComponent();
    };

    private void AddProjectAttributes(RenderTreeBuilder builder)
    {
        builder.AddAttribute(2, nameof(PipelineSetupWizardStep.Projects), _projects);
        builder.AddAttribute(3, nameof(PipelineSetupWizardStep.ProjectId), _selectedProjectId);
        builder.AddAttribute(4, nameof(PipelineSetupWizardStep.ProjectIdChanged),
            EventCallback.Factory.Create<int?>(this, OnProjectChanged));
        builder.AddAttribute(5, nameof(PipelineSetupWizardStep.Prefix), _prefix);
        builder.AddAttribute(6, nameof(PipelineSetupWizardStep.PrefixChanged),
            EventCallback.Factory.Create<string>(this, value => Update(value, next => _prefix = next)));
        builder.AddAttribute(8, nameof(PipelineSetupWizardStep.SourceBranch), BranchChoice.Chosen);
        builder.AddAttribute(9, nameof(PipelineSetupWizardStep.SourceBranchChanged),
            EventCallback.Factory.Create<string>(this, BranchChoice.Pick));
        builder.AddAttribute(180, nameof(PipelineSetupWizardStep.SourceBranches), BranchChoice.Options);
        builder.AddAttribute(181, nameof(PipelineSetupWizardStep.SourceBranchMissing), BranchChoice.IsMissing);
    }

    private void AddPipelineToggles(RenderTreeBuilder builder)
    {
        AddToggle(builder, 10, nameof(PipelineSetupWizardStep.Frontend), _frontend,
            nameof(PipelineSetupWizardStep.FrontendChanged), value => _frontend = value);
        AddToggle(builder, 20, nameof(PipelineSetupWizardStep.Backend), _backend,
            nameof(PipelineSetupWizardStep.BackendChanged), value => _backend = value);
        AddToggle(builder, 30, nameof(PipelineSetupWizardStep.Database), _database,
            nameof(PipelineSetupWizardStep.DatabaseChanged), value => _database = value);
        AddToggle(builder, 40, nameof(PipelineSetupWizardStep.Ci), _ci,
            nameof(PipelineSetupWizardStep.CiChanged), value => _ci = value);
        AddToggle(builder, 50, nameof(PipelineSetupWizardStep.Quality), _quality,
            nameof(PipelineSetupWizardStep.QualityChanged), value => _quality = value);
        AddToggle(builder, 60, nameof(PipelineSetupWizardStep.Security), _security,
            nameof(PipelineSetupWizardStep.SecurityChanged), value => _security = value);
        AddToggle(builder, 70, nameof(PipelineSetupWizardStep.Qa), _qa,
            nameof(PipelineSetupWizardStep.QaChanged), value =>
            {
                _qa = value;
                _qaTouched = true;
            });
        AddToggle(builder, 80, nameof(PipelineSetupWizardStep.Candidate), _candidate,
            nameof(PipelineSetupWizardStep.CandidateChanged), value => _candidate = value);
    }

    private void AddOptionToggles(RenderTreeBuilder builder)
    {
        AddToggle(builder, 90, nameof(PipelineSetupWizardStep.Nightly), _nightly,
            nameof(PipelineSetupWizardStep.NightlyChanged), value => _nightly = value);
        AddToggle(builder, 100, nameof(PipelineSetupWizardStep.Production), _production,
            nameof(PipelineSetupWizardStep.ProductionChanged), value => _production = value);
        AddToggle(builder, 110, nameof(PipelineSetupWizardStep.FastDeploy), _fastDeploy,
            nameof(PipelineSetupWizardStep.FastDeployChanged), value => _fastDeploy = value);
        AddToggle(builder, 120, nameof(PipelineSetupWizardStep.Rollback), _rollback,
            nameof(PipelineSetupWizardStep.RollbackChanged), value => _rollback = value);
        AddToggle(builder, 130, nameof(PipelineSetupWizardStep.Packages), _packages,
            nameof(PipelineSetupWizardStep.PackagesChanged), value => _packages = value);
        AddToggle(builder, 150, nameof(PipelineSetupWizardStep.QaExtended), _qaExtended,
            nameof(PipelineSetupWizardStep.QaExtendedChanged), value => _qaExtended = value);
        AddToggle(builder, 160, nameof(PipelineSetupWizardStep.SecurityHistory), _securityHistory,
            nameof(PipelineSetupWizardStep.SecurityHistoryChanged), value => _securityHistory = value);
        AddToggle(builder, 220, nameof(PipelineSetupWizardStep.DeployChain), _deployChain.RunAfterCandidate,
            nameof(PipelineSetupWizardStep.DeployChainChanged), value => _deployChain.RunAfterCandidate = value);
        AddToggle(builder, 190, nameof(PipelineSetupWizardStep.LangDotNet), _languages.DotNet,
            nameof(PipelineSetupWizardStep.LangDotNetChanged), value => PickLanguages(_languages with { DotNet = value }));
        AddToggle(builder, 200, nameof(PipelineSetupWizardStep.LangPython), _languages.Python,
            nameof(PipelineSetupWizardStep.LangPythonChanged), value => PickLanguages(_languages with { Python = value }));
        AddToggle(builder, 210, nameof(PipelineSetupWizardStep.LangJava), _languages.Java,
            nameof(PipelineSetupWizardStep.LangJavaChanged), value => PickLanguages(_languages with { Java = value }));
    }

    private void PickLanguages(PipelineSetupLanguages languages)
    {
        _languages = languages;
        _languagesTouched = true;
    }

    private void AddSummaryAttributes(RenderTreeBuilder builder)
    {
        builder.AddAttribute(140, nameof(PipelineSetupWizardStep.ProjectName), SelectedProject?.Name ?? string.Empty);
        builder.AddAttribute(141, nameof(PipelineSetupWizardStep.ArchitectureSummary), ArchitectureSummary);
        builder.AddAttribute(142, nameof(PipelineSetupWizardStep.SummaryItems), SelectedRoles
            .Select(role => new PipelineSetupSummaryItem(PipelineName(role), role.TemplateName)).ToArray());
        builder.AddAttribute(143, nameof(PipelineSetupWizardStep.Readiness), _readiness);
        builder.AddAttribute(144, nameof(PipelineSetupWizardStep.ReadinessLoading), _readinessLoading);
        builder.AddAttribute(145, nameof(PipelineSetupWizardStep.OnReadinessChanged),
            EventCallback.Factory.Create(this, RecheckReadinessAsync));
        builder.AddAttribute(146, nameof(PipelineSetupWizardStep.ReturnUrl), SummaryUrl);
        builder.AddAttribute(149, nameof(PipelineSetupWizardStep.ProductionSetup),
            On("production", _production) ? ProductionSetup : null);
        builder.AddAttribute(147, nameof(PipelineSetupWizardStep.DetectionWarnings), PipelineSetupDetectionText.Warnings(
            L, _detection, _detectionBranch, [.. SelectedRoles.Select(role => role.TemplateName)]));
        builder.AddAttribute(150, nameof(PipelineSetupWizardStep.LanguagesUncertain), PipelineSetupLanguages.IsUncertain(_detection));
        builder.AddAttribute(151, nameof(PipelineSetupWizardStep.DeployChainOffered), PipelineSetupDeployChain.IsOffered(CreatedSuffixes));
        builder.AddAttribute(148, nameof(PipelineSetupWizardStep.QaNoTestsTip),
            PipelineSetupDetectionText.QaNoTestsTip(L, _detection, _detectionBranch));
    }

    private void AddArchitectureAttributes(RenderTreeBuilder builder)
    {
        builder.AddAttribute(170, nameof(PipelineSetupWizardStep.Architecture), _architecture);
        builder.AddAttribute(171, nameof(PipelineSetupWizardStep.ArchitectureChanged),
            EventCallback.Factory.Create<PipelineArchitecture?>(this, OnArchitectureChanged));
        builder.AddAttribute(172, nameof(PipelineSetupWizardStep.Stack), _stack.Key);
        builder.AddAttribute(173, nameof(PipelineSetupWizardStep.StackChanged),
            EventCallback.Factory.Create<string>(this, key => Update(key, next => _stack = PipelineSetupStack.Find(next) ?? _stack)));
        builder.AddAttribute(174, nameof(PipelineSetupWizardStep.SameProject), _sameProject);
        builder.AddAttribute(175, nameof(PipelineSetupWizardStep.SameProjectChanged),
            EventCallback.Factory.Create<bool?>(this, value => Update(value, next => _sameProject = next)));
        AddToggle(builder, 176, nameof(PipelineSetupWizardStep.ExternalSecurity), _externalSecurity,
            nameof(PipelineSetupWizardStep.ExternalSecurityChanged), value => _externalSecurity = value);
    }

    private void AddToggle(
        RenderTreeBuilder builder,
        int sequence,
        string valueName,
        bool value,
        string callbackName,
        Action<bool> setter)
    {
        builder.AddAttribute(sequence, valueName, value);
        builder.AddAttribute(sequence + 1, callbackName,
            EventCallback.Factory.Create<bool>(this, next => Update(next, setter)));
    }

    private void Update<T>(T value, Action<T> setter)
    {
        setter(value);
        RefreshSteps();
    }

    /// <summary>A package type is what the package pipelines exist for: choosing it turns them on.
    /// Going from one type to another forgets what was chosen under the first (the application shape
    /// and every option), so nothing is created that was not chosen under the type that stays.</summary>
    private void OnArchitectureChanged(PipelineArchitecture? architecture)
    {
        var typeChanged = _architecture is not null && architecture != _architecture;
        _architecture = architecture;
        if (typeChanged)
            ResetOptions();
        if (architecture == PipelineArchitecture.Package)
            _packages = true;
        RefreshSteps();
    }

    /// <summary>Every shape and option back to what a type starts with.</summary>
    private void ResetOptions()
    {
        _sameProject = null;
        _frontend = _backend = _database = true;
        _ci = _quality = _security = _externalSecurity = true;
        var application = _architecture == PipelineArchitecture.Application;
        _qa = _candidate = _nightly = _production = _rollback = application;
        _qaExtended = _securityHistory = _fastDeploy = _packages = false;
        _deployChain.RunAfterCandidate = true;
        _qaTouched = false;
    }

    private string ArchitectureSummary =>
        PipelineSetupArchitecture.Summary(L, _architecture, _sameProject, _frontend, _backend, _database);

    private void OnProjectChanged(int? projectId)
    {
        // Another project: what was read from the previous one's branch says nothing about this one, and a QA
        // the detection had unticked goes back to its starting value until this project is read.
        if (projectId != _selectedProjectId)
        {
            _detection = null;
            _detectionBranch = null;
            _detectionProjectId = null;
            _productionSetup?.Reset();
            if (!_qaTouched) _qa = true;
            if (!_languagesTouched) _languages = PipelineSetupLanguages.Proposal(null, _stack);
        }
        _selectedProjectId = projectId;
        BranchChoice.Follow(SelectedProject);
        SetPrefixFromProject();
        RefreshSteps();
    }

    private void SetPrefixFromProject()
    {
        if (SelectedProject is { } project)
            _prefix = PipelinePrefix.FromName(project.Name);
    }

    private void SelectOnlyRequestedTemplate(int templateId)
    {
        var name = _templates.FirstOrDefault(template => template.Id == templateId)?.Name;
        if (name is null) return;
        _ci = name == "application-ci";
        _quality = name == "application-quality";
        _security = name == "application-security";
        _qa = name == "application-qa";
        _qaTouched = true;
        _qaExtended = name == "application-qa-extended";
        _securityHistory = name == "application-security-history";
        _candidate = name == "application-candidate";
        _nightly = name == "application-nightly";
        _production = name is "application-deploy-prod" or "application-promotion";
        _fastDeploy = name is "application-release-fast" or "application-light";
        _packages = name.StartsWith("package-", StringComparison.Ordinal)
            || name == "publish-observability-packages";
        // A package or external template says what is built; an application template does not say which kind of
        // application, so the type stays to be asked.
        _externalSecurity = name == "application-security-external";
        if (_packages)
            _architecture = PipelineArchitecture.Package;
        else if (_externalSecurity)
            _architecture = PipelineArchitecture.External;
    }

    private async Task OnStepChanged(int step)
    {
        _currentStep = step;
        if (_copy is { } copy)
        {
            await copy.OnStepChangedAsync(step);
            return;
        }
        if (step == CapabilitiesStep) await LoadDetectionAsync();
        RefreshSteps();
        if (step != SummaryStep) return;
        if (SummaryUrl is { } summaryUrl)
            Nav.NavigateTo(summaryUrl, replace: true);
        await LoadSummaryAsync();
    }

    /// <summary>
    /// Reads the project's branch when the Capabilities step opens, once per project: whether it holds a .NET
    /// solution, a test project a QA can run, a Compose file, and whether a runner reports Docker. A QA that
    /// would fail for lack of tests starts unticked, unless the user decided it himself.
    /// </summary>
    private async Task LoadDetectionAsync()
    {
        if (_selectedProjectId is { } projectId && _detectionProjectId != projectId)
        {
            _detectionProjectId = projectId;
            try
            {
                var verdict = await Api.Pipelines.CheckSetupReadinessAsync(projectId, PipelineSetupDetectionText.DetectedTemplates);
                _detection = verdict?.Detection;
                _detectionBranch = verdict?.InspectedBranch;
            }
            catch (HttpRequestException)
            {
                // Nothing is read, so nothing is claimed: no warning, and the starting options stay.
                _detection = null;
                _detectionBranch = null;
                _detectionProjectId = null;
            }
        }

        if (!_qaTouched && _architecture == PipelineArchitecture.Application && _detection?.HasDotNetTestProject is { } hasTests)
            _qa = hasTests;
        if (!_languagesTouched)
            _languages = PipelineSetupLanguages.Proposal(_detection, _stack);
    }

    /// <summary>The summary's two readings, in parallel: the readiness verdict and, when the production pipeline is
    /// selected, the Compose file of the repository.</summary>
    private async Task LoadSummaryAsync()
    {
        var production = (On("production", _production) || On("qa", _qa)) && _selectedProjectId is { } projectId
            ? ProductionSetup.LoadAsync(projectId)
            : Task.CompletedTask;
        await Task.WhenAll(LoadReadinessAsync(), production);
    }

    /// <summary>
    /// Loads the readiness verdict when the summary is reached, not earlier: the answer depends on
    /// the capabilities chosen on the previous step, so checking before they are settled would show
    /// findings for templates the user then deselects.
    ///
    /// The selection is fingerprinted so stepping back and forth without changing anything does not
    /// re-issue a check that reads the repository tree.
    /// </summary>
    private async Task LoadReadinessAsync(bool keepCurrent = false)
    {
        if (_selectedProjectId is not { } projectId) return;
        var names = SelectedRoles.Select(role => role.TemplateName).ToList();
        var signature = $"{projectId}:{string.Join(",", names)}";
        if (signature == _readinessSignature) return;

        _readinessSignature = signature;
        // A check run again after a creation keeps the verdict on screen under the panel's veil (R2-20).
        if (!keepCurrent) _readiness = null;
        _readinessLoading = true;
        RefreshSteps();
        try
        {
            _readiness = await Api.Pipelines.CheckSetupReadinessAsync(projectId, names);
        }
        catch (HttpRequestException)
        {
            // The panel renders "could not be checked" for a null verdict, which is the honest
            // outcome: no claim is made about a project state that was never read.
            _readiness = null;
            _readinessSignature = null;
        }
        finally
        {
            _readinessLoading = false;
            RefreshSteps();
        }
    }

    /// <summary>Creating required libraries and vaults invalidates the cached verdict, so the same
    /// selection must be checked again. The production reading too (R-77): it was made before the library <c>prod</c>
    /// existed, so it named no server of the project and claimed no entry, and the creation would have reserved no port.</summary>
    private async Task RecheckReadinessAsync()
    {
        _readinessSignature = null;
        var production = On("production", _production) || On("qa", _qa) ? ProductionSetup.ReloadAsync() : Task.CompletedTask;
        await Task.WhenAll(LoadReadinessAsync(keepCurrent: true), production);
    }

    private Task FinishAsync() => _copy is { } copy ? copy.FinishAsync(_currentStep) : CreatePipelinesAsync();

    private async Task CreatePipelinesAsync()
    {
        if (!CanAdvance || _selectedProjectId is not { } projectId) return;
        _saving = true;
        var created = new List<PipelineDto>();
        try
        {
            await BranchChoice.SettledAsync();
            await PrepareProductionAsync(projectId);
            foreach (var role in SelectedRoles)
            {
                var template = TemplateFor(role.TemplateName)
                    ?? throw new InvalidOperationException($"Missing pipeline template '{role.TemplateName}'.");
                var outcome = await Api.Pipelines.CreatePipelineAsync(new CreatePipelineRequest
                {
                    Name = PipelineName(role),
                    Description = string.Format(L["GeneratedPipelineDescription"], template.Name),
                    ProjectId = projectId,
                    SourceBranch = BranchChoice.Chosen,
                    YamlDefinition = BuildYaml(role, template, await _languages.ReadLintSourceAsync(Api, L, template))
                });
                if (!outcome.IsSuccess)
                    throw new InvalidOperationException(outcome.ErrorMessage
                        ?? L["OperationFailed"]);
                created.Add(outcome.Value!);
            }
            Toast.Success(string.Format(L["PipelinesCreated"], created.Count));
            if (_productionPending.Length > 0)
                Toast.Warning("ProductionValuesPending", "ProductionValuesPendingDetail", _productionPending);
            if (_productionNotPrepared)
                Toast.Warning("ProductionNotPrepared",
                    ProductionSetup.RepositoryRead ? "ProductionNoComposeDetail" : "ProductionNotPreparedDetail");
            if (On("qa", _qa) && string.IsNullOrEmpty(_productionInput?.ComposeFile))
                Toast.Warning("QaNoComposeFile", "QaNoComposeFileDetail");
            ProvisionState.Clear(projectId);
            // R-60: the end of the wizard lands on the project's overview, not on its pipelines.
            Nav.NavigateTo($"/projects/{projectId}");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            foreach (var pipeline in created.AsEnumerable().Reverse())
                await Api.Pipelines.DeletePipelineAsync(pipeline.Id);
            Toast.Error(exception.Message);
        }
        finally
        {
            _saving = false;
            RefreshSteps();
        }
    }

    /// <summary>ADR-057: before the production pipeline, the variables, secrets, ports and configuration repository its
    /// Compose file asks for. A project whose repository could not be read gets its pipeline without it. Everything
    /// the call does is idempotent, so a creation run again after a failure keeps what already exists.</summary>
    private async Task PrepareProductionAsync(int projectId)
    {
        _productionInput = null;
        _productionPending = string.Empty;
        _productionNotPrepared = false;
        if (!On("production", _production))
        {
            // Recette R-75: the QA deploys its disposable stack from the Compose file, production or not.
            if (!On("qa", _qa)) return;
            await ProductionSetup.SettledAsync();
            _productionInput = ProductionSetup.ForQaOnly();
            return;
        }

        // The reading of the repository may still be running (recette R-80): wait for it, never skip on it.
        await ProductionSetup.SettledAsync();
        if (!ProductionSetup.Ready)
        {
            // The Compose file was not read (a project with no commit yet is a new project): nothing of it is claimed, but the
            // servers of the project and of its production environment do not depend on it, and the end of the wizard says
            // that production was not prepared.
            _productionInput = ProductionSetup.WithoutComposeFile();
            _productionNotPrepared = true;
            return;
        }

        var outcome = await ProductionSetup.ApplyAsync(projectId);
        if (!outcome.IsSuccess)
            throw new InvalidOperationException(outcome.ErrorMessage ?? L["OperationFailed"]);
        var result = outcome.Value!;
        _productionInput = new PipelineSetupProductionInput(result.ComposeFile, result.Strategy, result.DeclaresApi, result.Server,
            [.. ProductionSetup.ServerChoices(result.Server)]);
        _productionPending = PipelineSetupProduction.PendingValues(result);
    }

    /// <summary>R2-11: the variables the template reads, only for the pipelines created.</summary>
    private string BuildYaml(PipelineRole role, PipelineTemplateSummaryDto template, string? lintSourceStage) =>
        PipelineSetupYamlBuilder.Build(PipelineName(role), template.Name, template.Version, new PipelineSetupYamlInput(
            _prefix,
            PipelineSetupArchitecture.Parts(_architecture, _sameProject, _frontend, _backend, _database),
            _stack.AnalyticsPackageTemplate,
            On("rollback", _rollback), On("quality", _quality), On("security", _security), On("qa", _qa),
            On("qa-extended", _qaExtended), On("security-history", _securityHistory),
            CreatedSuffixes, BranchChoice.Chosen, On("quality", _quality) ? _languages : null, lintSourceStage,
            _deployChain.RunAfterCandidate),
            _productionInput);

    /// <summary> The template the backend will resolve for this project (<see cref="PipelineSetupTemplateChoice"/>). </summary>
    private PipelineTemplateSummaryDto? TemplateFor(string name) =>
        PipelineSetupTemplateChoice.For(_templates, name, SelectedProject?.OrganizationId);

    private string PipelineName(PipelineRole role) => $"{_prefix}-{role.Suffix}";
    private void GoBack() => Nav.NavigateTo(BackHref);

    private sealed record PipelineRole(string Suffix, string TemplateName);
}
