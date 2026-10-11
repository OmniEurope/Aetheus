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

    // Recette R1-19: the summary with its choices travels in its own address (prefix, chosen options,
    // step), so a page opened from it to fix something comes back to the same summary. Nothing here is
    // sensitive: a pipeline name prefix and option names.
    [SupplyParameterFromQuery(Name = "prefix")] public string? PrefixQuery { get; set; }
    [SupplyParameterFromQuery(Name = "options")] public string? OptionsQuery { get; set; }
    [SupplyParameterFromQuery(Name = "step")] public string? StepQuery { get; set; }
    [SupplyParameterFromQuery(Name = "architecture")] public string? ArchitectureQuery { get; set; }
    [SupplyParameterFromQuery(Name = "sameProject")] public string? SameProjectQuery { get; set; }

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

    /// <summary>Recette R1-18 / R1-31: asked first, nothing chosen in advance (a default once gave a
    /// one-project Blazor Server application a separate frontend).</summary>
    private PipelineArchitecture? _architecture;
    /// <summary>Recette R1-31 decision: for an application, is the interface served by the server project?
    /// Yes, no, or not answered yet; no default answer.</summary>
    private bool? _sameProject;
    private PipelineSetupStack _stack = PipelineSetupStack.DotNet;
    private bool _frontend = true;
    private bool _backend = true;
    private bool _database;
    private bool _ci = true;
    private bool _quality = true;
    private bool _security = true;
    private bool _qa = true;
    private bool _qaExtended;
    private bool _securityHistory;
    private bool _candidate = true;
    private bool _nightly;
    private bool _production = true;
    private bool _fastDeploy;
    private bool _rollback = true;
    private bool _packages;
    private bool _externalSecurity = true;

    /// <summary>Index of the summary step, where the readiness check runs.</summary>
    private const int SummaryStep = 3;
    private PipelineSetupReadinessDto? _readiness;
    private bool _readinessLoading;
    private string? _readinessSignature;

    /// <summary>Recette R1-29: start from an existing project instead of the templates.</summary>
    private bool _copyMode;

    private IReadOnlyList<OmniOption<bool>> StartOptions =>
    [
        new OmniOption<bool>(false, L["PipelineSetupStartTemplates"].Value),
        new OmniOption<bool>(true, L["PipelineSetupStartExistingProject"].Value)
    ];

    private void OnModeChanged(bool copyMode) => _copyMode = copyMode;

    private string BackHref => _selectedProjectId is { } id ? $"/projects/{id}/pipelines" : "/pipelines";
    private ProjectDto? SelectedProject => _projects.FirstOrDefault(project => project.Id == _selectedProjectId);
    private bool CanAdvance => !_saving && _currentStep switch
    {
        0 => _selectedProjectId.HasValue && !string.IsNullOrWhiteSpace(_prefix),
        1 => PipelineSetupArchitecture.IsComplete(_architecture, _sameProject, _frontend, _backend),
        2 => SelectedRoles.Count > 0,
        _ => _selectedProjectId is not { } projectId || ProvisionState.AllChecked(projectId)
    };

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
            Add(On("production", _production), "deploy-prod", "application-deploy-prod");
            Add(On("fast-deploy", _fastDeploy), "release-fast", "application-release-fast");
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
        if (_initialStep == SummaryStep) await LoadReadinessAsync();
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
        ("external-security", () => _externalSecurity, value => _externalSecurity = value)
    ];

    /// <summary>
    /// Recette R1-19: the address of this summary with the user's choices. The pages the summary opens
    /// to fix something come back to it, and it replaces the wizard's own entry in the history, so the
    /// browser's Back lands on the same summary too.
    /// </summary>
    private string? SummaryUrl => _selectedProjectId is { } projectId
        ? $"/pipelines/setup?projectId={projectId}&prefix={Uri.EscapeDataString(_prefix)}"
            + $"&options={Uri.EscapeDataString(string.Join(",", Choices.Where(choice => choice.Get() && InAddress(choice.Name)).Select(choice => choice.Name)))}"
            + (_architecture is { } architecture ? $"&architecture={PipelineSetupArchitecture.Slug(architecture)}" : string.Empty)
            + (_architecture == PipelineArchitecture.Application && _sameProject is { } same ? $"&sameProject={(same ? "yes" : "no")}" : string.Empty)
            + $"&step={SummaryStepSlug}"
        : null;

    /// <summary>The address names the parts of an application and what the type proposes, not an
    /// option the type hides.</summary>
    private bool InAddress(string name) =>
        name is "frontend" or "backend" or "database" || PipelineSetupArchitecture.Proposes(_architecture, name);

    /// <summary>Takes back the choices the summary's address carries; an option it does not name is off.
    /// The summary is reopened only for a project and a selection that can reach it.</summary>
    private void RestoreChoices()
    {
        var chosen = OptionsQuery!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in Choices)
            choice.Set(chosen.Contains(choice.Name));
        if (!string.IsNullOrWhiteSpace(PrefixQuery))
            _prefix = PrefixQuery.Trim();
        // An address written before the type existed named the parts only: those were the separate
        // frontend and backend options, so it reopens as an application whose interface is not served by
        // the server project, with the same pipelines.
        var legacy = ArchitectureQuery is null && (_frontend || _backend || _database);
        _architecture = PipelineSetupArchitecture.FromSlug(ArchitectureQuery)
            ?? (legacy ? PipelineArchitecture.Application : null);
        _sameProject = legacy ? false : ParseSameProject(SameProjectQuery);
        if (CanReopenSummary())
        {
            _initialStep = SummaryStep;
            _currentStep = SummaryStep;
        }
    }

    private static bool? ParseSameProject(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" => true,
        "no" => false,
        _ => null
    };

    private bool CanReopenSummary() =>
        string.Equals(StepQuery, SummaryStepSlug, StringComparison.OrdinalIgnoreCase)
        && SelectedProject is not null && !string.IsNullOrWhiteSpace(_prefix)
        && PipelineSetupArchitecture.IsComplete(_architecture, _sameProject, _frontend, _backend) && SelectedRoles.Count > 0;

    private void RefreshSteps()
    {
        _steps =
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
        builder.AddAttribute(2, nameof(PipelineSetupWizardStep.Projects), _projects);
        builder.AddAttribute(3, nameof(PipelineSetupWizardStep.ProjectId), _selectedProjectId);
        builder.AddAttribute(4, nameof(PipelineSetupWizardStep.ProjectIdChanged),
            EventCallback.Factory.Create<int?>(this, OnProjectChanged));
        builder.AddAttribute(5, nameof(PipelineSetupWizardStep.Prefix), _prefix);
        builder.AddAttribute(6, nameof(PipelineSetupWizardStep.PrefixChanged),
            EventCallback.Factory.Create<string>(this, value => Update(value, next => _prefix = next)));
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
            nameof(PipelineSetupWizardStep.QaChanged), value => _qa = value);
        AddToggle(builder, 80, nameof(PipelineSetupWizardStep.Candidate), _candidate,
            nameof(PipelineSetupWizardStep.CandidateChanged), value => _candidate = value);
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
        builder.AddAttribute(140, nameof(PipelineSetupWizardStep.ProjectName), SelectedProject?.Name ?? string.Empty);
        builder.AddAttribute(141, nameof(PipelineSetupWizardStep.ArchitectureSummary), ArchitectureSummary);
        builder.AddAttribute(142, nameof(PipelineSetupWizardStep.SummaryItems), SelectedRoles
            .Select(role => new PipelineSetupSummaryItem(PipelineName(role), role.TemplateName)).ToArray());
        builder.AddAttribute(143, nameof(PipelineSetupWizardStep.Readiness), _readiness);
        builder.AddAttribute(144, nameof(PipelineSetupWizardStep.ReadinessLoading), _readinessLoading);
        builder.AddAttribute(145, nameof(PipelineSetupWizardStep.OnReadinessChanged),
            EventCallback.Factory.Create(this, RecheckReadinessAsync));
        builder.AddAttribute(146, nameof(PipelineSetupWizardStep.ReturnUrl), SummaryUrl);
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
        builder.CloseComponent();
    };

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

    /// <summary>A package type is what the package pipelines exist for: choosing it turns them on.</summary>
    private void OnArchitectureChanged(PipelineArchitecture? architecture)
    {
        _architecture = architecture;
        if (architecture == PipelineArchitecture.Package)
            _packages = true;
        RefreshSteps();
    }

    private string ArchitectureSummary
    {
        get
        {
            if (_architecture is not { } architecture) return string.Empty;
            var parts = PipelineSetupArchitecture.Parts(_architecture, _sameProject, _frontend, _backend, _database);
            return string.Join(" · ", new[]
            {
                L[PipelineSetupArchitecture.LabelKeys(architecture).Title].Value,
                architecture == PipelineArchitecture.Application && _sameProject == true ? L["PipelineSameProjectSummary"].Value : null,
                parts.Frontend ? L["Frontend"].Value : null,
                _sameProject == false && parts.Backend ? L["Backend"].Value : null,
                parts.Database ? L["Database"].Value : null
            }.Where(value => value is not null));
        }
    }

    private void OnProjectChanged(int? projectId)
    {
        _selectedProjectId = projectId;
        SetPrefixFromProject();
        RefreshSteps();
    }

    private void SetPrefixFromProject()
    {
        if (SelectedProject is { } project)
            _prefix = Slug(project.Name);
    }

    private void SelectOnlyRequestedTemplate(int templateId)
    {
        var name = _templates.FirstOrDefault(template => template.Id == templateId)?.Name;
        if (name is null) return;
        _ci = name == "application-ci";
        _quality = name == "application-quality";
        _security = name == "application-security";
        _qa = name == "application-qa";
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
        RefreshSteps();
        if (step != SummaryStep) return;
        if (SummaryUrl is { } summaryUrl)
            Nav.NavigateTo(summaryUrl, replace: true);
        await LoadReadinessAsync();
    }

    /// <summary>
    /// Loads the readiness verdict when the summary is reached, not earlier: the answer depends on
    /// the capabilities chosen on the previous step, so checking before they are settled would show
    /// findings for templates the user then deselects.
    ///
    /// The selection is fingerprinted so stepping back and forth without changing anything does not
    /// re-issue a check that reads the repository tree.
    /// </summary>
    private async Task LoadReadinessAsync()
    {
        if (_selectedProjectId is not { } projectId) return;
        var names = SelectedRoles.Select(role => role.TemplateName).ToList();
        var signature = $"{projectId}:{string.Join(",", names)}";
        if (signature == _readinessSignature) return;

        _readinessSignature = signature;
        _readiness = null;
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

    /// <summary>PLAN-003 lot 30: after the required libraries and vaults were created, the previous
    /// verdict is stale; forget its fingerprint so the same selection is checked again.</summary>
    private async Task RecheckReadinessAsync()
    {
        _readinessSignature = null;
        await LoadReadinessAsync();
    }

    private async Task CreatePipelinesAsync()
    {
        if (!CanAdvance || _selectedProjectId is not { } projectId) return;
        _saving = true;
        var created = new List<PipelineDto>();
        try
        {
            foreach (var role in SelectedRoles)
            {
                var template = TemplateFor(role.TemplateName)
                    ?? throw new InvalidOperationException($"Missing pipeline template '{role.TemplateName}'.");
                var outcome = await Api.Pipelines.CreatePipelineAsync(new CreatePipelineRequest
                {
                    Name = PipelineName(role),
                    Description = string.Format(L["GeneratedPipelineDescription"], template.Name),
                    ProjectId = projectId,
                    SourceBranch = SelectedProject?.DefaultBranch,
                    YamlDefinition = BuildYaml(role, template)
                });
                if (!outcome.IsSuccess)
                    throw new InvalidOperationException(outcome.ErrorMessage
                        ?? L["OperationFailed"]);
                created.Add(outcome.Value!);
            }
            Toast.Success(string.Format(L["PipelinesCreated"], created.Count));
            ProvisionState.Clear(projectId);
            Nav.NavigateTo($"/projects/{projectId}/pipelines");
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

    private string BuildYaml(PipelineRole role, PipelineTemplateSummaryDto template)
    {
        var parts = PipelineSetupArchitecture.Parts(_architecture, _sameProject, _frontend, _backend, _database);
        return $"""
        name: {PipelineName(role)}
        extends: {template.Name}@{template.Version}
        trigger: manual
        variables:
          APPLICATION_PREFIX: "{_prefix}"
          APPLICATION_HAS_FRONTEND: "{Flag(parts.Frontend)}"
          APPLICATION_HAS_BACKEND: "{Flag(parts.Backend)}"
          APPLICATION_HAS_DATABASE: "{Flag(parts.Database)}"
          APPLICATION_ROLLBACK_ENABLED: "{Flag(On("rollback", _rollback))}"
          APPLICATION_QUALITY_ENABLED: "{Flag(On("quality", _quality))}"
          APPLICATION_SECURITY_ENABLED: "{Flag(On("security", _security))}"
          APPLICATION_QA_ENABLED: "{Flag(On("qa", _qa))}"
          APPLICATION_QA_EXTENDED_ENABLED: "{Flag(On("qa-extended", _qaExtended))}"
          APPLICATION_SECURITY_HISTORY_ENABLED: "{Flag(On("security-history", _securityHistory))}"
          APPLICATION_CI_PIPELINE: "{_prefix}-ci"
          APPLICATION_QUALITY_PIPELINE: "{_prefix}-quality"
          APPLICATION_SECURITY_PIPELINE: "{_prefix}-security"
          APPLICATION_QA_PIPELINE: "{_prefix}-qa"
          APPLICATION_QA_EXTENDED_PIPELINE: "{_prefix}-qa-extended"
          APPLICATION_SECURITY_HISTORY_PIPELINE: "{_prefix}-security-history"
          APPLICATION_CANDIDATE_PIPELINE: "{_prefix}-candidate"
          APPLICATION_DEPLOY_PIPELINE: "{_prefix}-deploy-prod"
          APPLICATION_PACKAGE_TELEMETRY_PIPELINE: "{_prefix}-package-telemetry"
          APPLICATION_PACKAGE_DOTNET_ANALYTICS_PIPELINE: "{_prefix}-{_stack.AnalyticsPackageTemplate}"
          APPLICATION_PACKAGE_BROWSER_ANALYTICS_PIPELINE: "{_prefix}-package-web-analytics-browser"
        stages: []
        """;

        static string Flag(bool value) => value ? "true" : "false";
    }

    /// <summary>
    /// Recette R2-121: the template the backend will resolve for this project, so the pinned version
    /// written into <c>extends:</c> is that template's. The project's organization's own template wins;
    /// otherwise the shipped one the platform shares with every organization.
    /// </summary>
    private PipelineTemplateSummaryDto? TemplateFor(string name) => _templates
        .Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(item => item.OrganizationId == SelectedProject?.OrganizationId)
        .FirstOrDefault();

    private string PipelineName(PipelineRole role) => $"{_prefix}-{role.Suffix}";
    private void GoBack() => Nav.NavigateTo(BackHref);

    internal static string Slug(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }

    private sealed record PipelineRole(string Suffix, string TemplateName);
}
