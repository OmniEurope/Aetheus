// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Environments;

public partial class EnvironmentEdit
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    [Parameter] public int? Id { get; set; }

    [SupplyParameterFromQuery] public int? ProjectId { get; set; }

    /// <summary>The page that sent the user here to create an environment (the pipeline setup
    /// wizard's summary, with its choices); "Create" and Back return to it when it is
    /// local.</summary>
    [SupplyParameterFromQuery(Name = LocalReturnUrl.Parameter)] public string? ReturnUrl { get; set; }

    private EnvironmentDto? _detail;
    private EnvironmentModel _model = new();
    private List<ProjectDto> _projects = [];
    private List<ServerDto> _servers = [];
    private List<EnvironmentCopySource> _copySources = [];
    private int? _sourceEnvironmentId;
    private bool _isNew => Id is null or 0;
    private bool _saving;
    private int? _previousId = int.MinValue;
    private int? _previousProjectId = int.MinValue;
    private int _loadGeneration;
    // FRONT-001: an existing environment that answers 404 or cannot be fetched ends the loading
    // state instead of leaving the page on the loader forever.
    private bool _notFound;
    private bool _loadFailed;

    private List<EnvironmentTypeOption> _typeOptions = [];

    private readonly record struct EnvironmentTypeOption(string Text, EnvironmentType Value);
    private readonly record struct EnvironmentCopySource(int Id, string Label, EnvironmentDto Environment);

    /// <summary>
    /// R-70: the source environments listed under their project (a native list renders each group as an
    /// optgroup). The label keeps the project too, because a closed list shows only the chosen option's text.
    /// </summary>
    private IReadOnlyList<OmniOption<int?>> CopySourceOptions =>
    [
        .. _copySources.Select(source => new OmniOption<int?>(
            source.Id, source.Label, Group: source.Environment.ProjectName ?? L["NoProject"]))
    ];

    protected override void OnInitialized()
    {
        _typeOptions = Enum.GetValues<EnvironmentType>()
            .Select(value => new EnvironmentTypeOption(L.Localize(value), value))
            .ToList();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Id == _previousId && ProjectId == _previousProjectId) return;
        _previousId = Id;
        _previousProjectId = ProjectId;
        var generation = Interlocked.Increment(ref _loadGeneration);

        _detail = null;
        _notFound = false;
        _loadFailed = false;
        _model = new EnvironmentModel();
        _sourceEnvironmentId = null;
        _copySources = [];

        try
        {
            var projectsTask = Api.Projects.GetAllProjectsAsync();
            var serversTask = Api.Servers.GetAllServersAsync();
            await Task.WhenAll(projectsTask, serversTask);
            if (generation != _loadGeneration) return;
            _projects = await projectsTask;
            _servers = await serversTask;

            if (!_isNew)
            {
                _detail = await Api.Servers.GetEnvironmentAsync(Id!.Value);
                if (generation != _loadGeneration) return;
                _notFound = _detail is null;
                if (_detail is not null)
                {
                    _model = new EnvironmentModel
                    {
                        Name = _detail.Name,
                        Description = _detail.Description,
                        Type = _detail.Type,
                        ProjectId = _detail.ProjectId,
                        RequireApproval = _detail.RequireApproval,
                        ApprovalTimeoutMinutes = _detail.ApprovalTimeoutMinutes,
                        ApprovalInstructions = _detail.ApprovalInstructions,
                        DastEnabled = _detail.DastEnabled,
                        DastIsEphemeral = _detail.DastIsEphemeral,
                        DastContainsRealData = _detail.DastContainsRealData,
                        DastAllowedHosts = _detail.DastAllowedHosts,
                        ServerIds = _detail.Servers.Select(s => s.ServerId).ToList()
                    };
                }
            }
            else if (ProjectId is > 0 && _projects.Any(p => p.Id == ProjectId.Value))
            {
                _model.ProjectId = ProjectId;
            }

            if (_isNew)
            {
                await LoadCopySourcesAsync(generation);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (generation != _loadGeneration) return;
            _notFound = !_isNew;
        }
        catch (HttpRequestException)
        {
            // 401 on expired JWT - redirect handled by AuthProvider; any other failure is shown.
            if (generation != _loadGeneration) return;
            _loadFailed = !_isNew;
        }

        ProjectNav.Set(_model.ProjectId);

        ReassertBreadcrumb();
    }

    private async Task LoadCopySourcesAsync(int generation)
    {
        try
        {
            var environments = await Api.Servers.GetAllEnvironmentsAsync();
            if (generation != _loadGeneration) return;
            _copySources = environments
                .OrderBy(environment => environment.ProjectName ?? string.Empty)
                .ThenBy(environment => environment.Name)
                .Select(environment => new EnvironmentCopySource(
                    environment.Id,
                    $"{environment.Name} · {environment.ProjectName ?? L["NoProject"]}",
                    environment))
                .ToList();
        }
        catch (HttpRequestException)
        {
            // Expired authentication is handled centrally. The empty-create flow remains usable
            // when the optional source catalogue cannot be loaded.
        }
    }

    private void OnSourceEnvironmentChanged(object? value)
    {
        _sourceEnvironmentId = value switch
        {
            int id => id,
            long id => checked((int)id),
            string text when int.TryParse(text, out var id) => id,
            _ => null
        };

        var targetProjectId = _model.ProjectId;
        var source = _copySources.FirstOrDefault(option => option.Id == _sourceEnvironmentId).Environment;
        if (source is null)
        {
            _model = new EnvironmentModel { ProjectId = targetProjectId };
        }
        else
        {
            _model = new EnvironmentModel
            {
                Name = source.ProjectId == targetProjectId ? $"{source.Name} (copy)" : source.Name,
                // The source's description describes the source, not the new environment.
                Description = string.Empty,
                Type = source.Type,
                ProjectId = targetProjectId,
                RequireApproval = source.RequireApproval,
                ApprovalTimeoutMinutes = source.ApprovalTimeoutMinutes,
                ApprovalInstructions = source.ApprovalInstructions,
                DastEnabled = source.DastEnabled,
                DastIsEphemeral = source.DastIsEphemeral,
                DastContainsRealData = source.DastContainsRealData,
                DastAllowedHosts = source.DastAllowedHosts,
                ServerIds = source.Servers.Select(server => server.ServerId).ToList()
            };
        }

        ProjectNav.Set(_model.ProjectId);
        ReassertBreadcrumb();
    }

    private void ReassertBreadcrumb()
    {
        var current = new BreadcrumbItem(_isNew ? L["NewEnvironment"] : _detail?.Name ?? L["Environment"]);
        Breadcrumb.SetProjectResource(
            _model.ProjectId, _detail?.ProjectName, _projects,
            L["Projects"], L["Project"], L["Environments"],
            "environments", "/environments", current);
    }

    private async Task OnSubmit()
    {
        _saving = true;
        if (_isNew)
        {
            var request = BuildRequest<CreateEnvironmentRequest>() with
            {
                SourceEnvironmentId = _sourceEnvironmentId
            };
            var created = await Api.Servers.CreateEnvironmentAsync(request);
            if (created is not null)
            {
                Toast.Success("Created", "EnvironmentCreated");
                Nav.NavigateTo(LocalReturnUrl.Validated(ReturnUrl) ?? $"/environments/{created.Id}");
            }
        }
        else
        {
            var updated = await Api.Servers.UpdateEnvironmentAsync(
                Id!.Value, BuildRequest<UpdateEnvironmentRequest>());
            if (updated is not null)
            {
                Toast.Success("Saved", "EnvironmentSaved");
                _detail = updated;
            }
        }
        _saving = false;
    }

    private TRequest BuildRequest<TRequest>()
        where TRequest : EnvironmentRequest, new() => new()
        {
            Name = _model.Name,
            Description = _model.Description,
            Type = _model.Type,
            ProjectId = _model.ProjectId,
            RequireApproval = _model.RequireApproval,
            ApprovalTimeoutMinutes = _model.ApprovalTimeoutMinutes,
            ApprovalInstructions = _model.ApprovalInstructions,
            DastEnabled = _model.DastEnabled,
            DastIsEphemeral = _model.DastIsEphemeral,
            DastContainsRealData = _model.DastContainsRealData,
            DastAllowedHosts = _model.DastAllowedHosts,
            ServerIds = _model.ServerIds.ToList()
        };

    private async Task OnDelete()
    {
        var confirmed = await Dialog.Confirm(L["DeleteEnvironmentConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var status = await Api.Servers.DeleteEnvironmentAsync(Id!.Value);
            if (status.Success)
            {
                Cache.InvalidatePrefix("environments:"); // S-TECH-SWIV
                Toast.Success("Deleted", "Deleted");
                Nav.NavigateTo("/environments");
            }
            else
            {
                Toast.Error("Error", "DeleteFailed");
            }
        });
    }

    private static OmniTone TypeStyle(EnvironmentType type) => type switch
    {
        EnvironmentType.Production => OmniTone.Danger,
        EnvironmentType.Staging => OmniTone.Warning,
        EnvironmentType.Testing => OmniTone.Accent,
        _ => OmniTone.Success
    };

    private class EnvironmentModel
    {
        // Default DataAnnotations messages - LocalizedDataAnnotationsValidator translates them
        // (Validation_Required / Validation_StringLength). A custom ErrorMessage would bypass that.
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [StringLength(500)]
        public string Description { get; set; } = string.Empty;

        private EnvironmentType _type = EnvironmentType.Development;

        /// <summary>R-70: choosing Production checks "Approval required" by default; the user can uncheck it
        /// afterwards. Initializers that also set <see cref="RequireApproval"/> list it after the type, so a
        /// stored or copied choice always wins.</summary>
        public EnvironmentType Type
        {
            get => _type;
            set
            {
                if (value == EnvironmentType.Production && _type != EnvironmentType.Production)
                    RequireApproval = true;
                _type = value;
            }
        }

        public int? ProjectId { get; set; }

        public bool RequireApproval { get; set; }

        [Range(1, 10080)]
        public int ApprovalTimeoutMinutes { get; set; } = 1440;

        [StringLength(1000)]
        public string? ApprovalInstructions { get; set; }

        public bool DastEnabled { get; set; }
        public bool DastIsEphemeral { get; set; }
        public bool DastContainsRealData { get; set; }

        [StringLength(2000)]
        public string DastAllowedHosts { get; set; } = string.Empty;

        public IReadOnlyList<int> ServerIds { get; set; } = [];
    }
}
