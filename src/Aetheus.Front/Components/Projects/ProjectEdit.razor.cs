// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

public partial class ProjectEdit
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private UiActions Ui { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private ActiveOrganizationService Organizations { get; set; } = default!;

    [Parameter] public int? Id { get; set; }

    internal ProjectModel _model = new();
    private bool _isNew => Id is null or 0;
    internal bool _saving;
    private int? _previousId = int.MinValue;

    protected override void OnParametersSet()
    {
        // Redirect existing project detail to the new path-based routes.
        // The /projects/{Id} route now serves only as a redirector.
        if (!_isNew)
        {
            Nav.NavigateTo($"/projects/{Id}/overview", replace: true);
            return;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_isNew) return; // Redirect handled above

        if (Id == _previousId) return;
        _previousId = Id;

        // Default a new project to "main" (case-insensitive master fallback happens later once the
        // repo has branches to detect); the user can still change it.
        _model = new ProjectModel { DefaultBranch = "main" };

        Breadcrumb.Set(
            new BreadcrumbItem(L["Projects"], "/projects"),
            new BreadcrumbItem(L["NewProject"]));

        await Task.CompletedTask;
    }

    internal async Task OnSubmit()
    {
        _saving = true;
        var tags = _model.TagsRaw
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        await Ui.RunAsync(
            () => Api.Projects.CreateProjectAsync(new CreateProjectRequest
            {
                Name = _model.Name,
                Description = _model.Description,
                RepositoryUrl = string.IsNullOrWhiteSpace(_model.RepositoryUrl) ? null : _model.RepositoryUrl,
                DefaultBranch = string.IsNullOrWhiteSpace(_model.DefaultBranch) ? null : _model.DefaultBranch,
                Tags = tags,
                // ADR-059: the project belongs to the organization the user is working in.
                OrganizationId = Organizations.Active?.Id
            }),
            successKey: "ProjectCreated",
            successTitleKey: "Created",
            onSuccess: created =>
            {
                Nav.NavigateTo($"/projects/{created.Id}");
                return Task.CompletedTask;
            });
        _saving = false;
    }

    internal class ProjectModel
    {
        // Default DataAnnotations messages - LocalizedDataAnnotationsValidator translates them
        // (Validation_Required / Validation_StringLength). A custom ErrorMessage would bypass that.
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [StringLength(500)]
        public string Description { get; set; } = string.Empty;

        public string RepositoryUrl { get; set; } = string.Empty;
        public string DefaultBranch { get; set; } = string.Empty;
        public ProjectStatus Status { get; set; } = ProjectStatus.Active;
        public string TagsRaw { get; set; } = string.Empty;
    }
}
