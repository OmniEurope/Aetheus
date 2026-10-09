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

    private readonly List<ProjectDto> _projects = [];
    private readonly List<PipelineTemplateSummaryDto> _templates = [];
    private List<WizardStep> _steps = [];
    private int _currentStep;
    private bool _loading = true;
    private bool _saving;
    private int? _selectedProjectId;
    private string _prefix = string.Empty;
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

    /// <summary>Index of the summary step, where the readiness check runs.</summary>
    private const int SummaryStep = 3;
    private PipelineSetupReadinessDto? _readiness;
    private bool _readinessLoading;
    private string? _readinessSignature;

    private string BackHref => _selectedProjectId is { } id ? $"/projects/{id}/pipelines" : "/pipelines";
    private ProjectDto? SelectedProject => _projects.FirstOrDefault(project => project.Id == _selectedProjectId);
    private bool CanAdvance => !_saving && _currentStep switch
    {
        0 => _selectedProjectId.HasValue && !string.IsNullOrWhiteSpace(_prefix),
        1 => _frontend || _backend || _database,
        2 => SelectedRoles.Count > 0,
        _ => true
    };

    private IReadOnlyList<PipelineRole> SelectedRoles
    {
        get
        {
            var roles = new List<PipelineRole>();
            var requiresCi = _ci || _quality || _security || _qa || _qaExtended || _securityHistory
                || _candidate || _nightly;
            Add(requiresCi, "ci", "application-ci");
            Add(_quality, "quality", "application-quality");
            Add(_security, "security", "application-security");
            Add(_qa, "qa", "application-qa");
            Add(_securityHistory, "security-history", "application-security-history");
            Add(_qaExtended, "qa-extended", "application-qa-extended");
            Add(_candidate || _nightly, "candidate", "application-candidate");
            Add(_nightly, "nightly", "application-nightly");
            Add(_production, "deploy-prod", "application-deploy-prod");
            Add(_fastDeploy, "release-fast", "application-release-fast");
            if (_packages)
            {
                Add(true, "package-telemetry", "package-telemetry");
                Add(true, "package-web-analytics-dotnet", "package-web-analytics-dotnet");
                Add(true, "package-web-analytics-browser", "package-web-analytics-browser");
                Add(true, "publish-observability-packages", "publish-observability-packages");
            }
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
            if (TemplateId is { } templateId)
                SelectOnlyRequestedTemplate(templateId);
        }
        catch (HttpRequestException)
        {
            Toast.Error(L["LoadFailed"]);
        }
        RefreshSteps();
        _loading = false;
    }

    private void RefreshSteps()
    {
        _steps =
        [
            new() { Title = L["Project"], Content = RenderStep(0) },
            new() { Title = L["ApplicationArchitecture"], Content = RenderStep(1) },
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
        builder.AddAttribute(145, nameof(PipelineSetupWizardStep.OnRequirementsProvisioned),
            EventCallback.Factory.Create(this, RecheckReadinessAsync));
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

    private string ArchitectureSummary => string.Join(" · ", new[]
    {
        _frontend ? L["Frontend"].Value : null,
        _backend ? L["Backend"].Value : null,
        _database ? L["Database"].Value : null
    }.Where(value => value is not null));

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
    }

    private async Task OnStepChanged(int step)
    {
        _currentStep = step;
        RefreshSteps();
        if (step == SummaryStep) await LoadReadinessAsync();
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

    private string BuildYaml(PipelineRole role, PipelineTemplateSummaryDto template) => $"""
        name: {PipelineName(role)}
        extends: {template.Name}@{template.Version}
        trigger: manual
        variables:
          APPLICATION_PREFIX: "{_prefix}"
          APPLICATION_HAS_FRONTEND: "{_frontend.ToString().ToLowerInvariant()}"
          APPLICATION_HAS_BACKEND: "{_backend.ToString().ToLowerInvariant()}"
          APPLICATION_HAS_DATABASE: "{_database.ToString().ToLowerInvariant()}"
          APPLICATION_ROLLBACK_ENABLED: "{_rollback.ToString().ToLowerInvariant()}"
          APPLICATION_QUALITY_ENABLED: "{_quality.ToString().ToLowerInvariant()}"
          APPLICATION_SECURITY_ENABLED: "{_security.ToString().ToLowerInvariant()}"
          APPLICATION_QA_ENABLED: "{_qa.ToString().ToLowerInvariant()}"
          APPLICATION_QA_EXTENDED_ENABLED: "{_qaExtended.ToString().ToLowerInvariant()}"
          APPLICATION_SECURITY_HISTORY_ENABLED: "{_securityHistory.ToString().ToLowerInvariant()}"
          APPLICATION_CI_PIPELINE: "{_prefix}-ci"
          APPLICATION_QUALITY_PIPELINE: "{_prefix}-quality"
          APPLICATION_SECURITY_PIPELINE: "{_prefix}-security"
          APPLICATION_QA_PIPELINE: "{_prefix}-qa"
          APPLICATION_QA_EXTENDED_PIPELINE: "{_prefix}-qa-extended"
          APPLICATION_SECURITY_HISTORY_PIPELINE: "{_prefix}-security-history"
          APPLICATION_CANDIDATE_PIPELINE: "{_prefix}-candidate"
          APPLICATION_DEPLOY_PIPELINE: "{_prefix}-deploy-prod"
          APPLICATION_PACKAGE_TELEMETRY_PIPELINE: "{_prefix}-package-telemetry"
          APPLICATION_PACKAGE_DOTNET_ANALYTICS_PIPELINE: "{_prefix}-package-web-analytics-dotnet"
          APPLICATION_PACKAGE_BROWSER_ANALYTICS_PIPELINE: "{_prefix}-package-web-analytics-browser"
        stages: []
        """;

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

    private static string Slug(string value)
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
