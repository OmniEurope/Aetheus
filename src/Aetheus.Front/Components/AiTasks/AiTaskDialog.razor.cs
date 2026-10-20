// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.AiTasks;

public partial class AiTaskDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;

    [Parameter] public int? DefinitionId { get; set; }
    [Parameter] public int? FixedProjectId { get; set; }

    private bool IsEdit => DefinitionId.HasValue;
    private EditModel _model = new();
    private List<AiRunnerProfileDto> _profiles = [];
    private List<ProjectDto> _projects = [];
    private List<ServerDto> _servers = [];
    private List<OmniOption<string>> _ownerTypes = [];
    private bool _loading;
    private bool _busy;

    protected override async Task OnInitializedAsync()
    {
        _loading = true;
        try
        {
            _ownerTypes =
            [
                new("project", L["Project"].Value),
                new("server", L["Server"].Value)
            ];
            var profilesTask = Api.Ai.GetAiRunnerProfileOptionsAsync();
            var projectsTask = Api.Projects.GetAllProjectsAsync();
            var serversTask = Api.Servers.GetAllServersAsync();
            await Task.WhenAll(profilesTask, projectsTask, serversTask);
            _profiles = profilesTask.Result;
            _projects = projectsTask.Result;
            _servers = serversTask.Result;
            _model.ProjectId = FixedProjectId;
            _model.ProfileId = _profiles.FirstOrDefault()?.Id ?? 0;

            if (DefinitionId.HasValue && await Api.Ai.GetAiTaskAsync(DefinitionId.Value) is { } definition)
            {
                _model = new EditModel
                {
                    Name = definition.Name,
                    ProfileId = definition.ProfileId,
                    PromptTemplate = definition.PromptTemplate,
                    ProjectId = definition.ProjectId,
                    ServerId = definition.ServerId,
                    OwnerType = definition.ProjectId.HasValue ? "project" : "server",
                    Schedule = definition.Schedule ?? string.Empty,
                    EventTypes = string.Join(", ", definition.EventTypes),
                    ContextPaths = string.Join("\n", definition.ContextPaths),
                    GitRange = definition.GitRange ?? string.Empty,
                    Enabled = definition.Enabled
                };
            }
        }
        catch (HttpRequestException)
        {
            Notify.Error("Error", "LoadFailed");
        }
        finally
        {
            _loading = false;
        }
    }

    private IReadOnlyList<string> ContextPathList =>
        _model.ContextPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void OnContextPathsChanged(IReadOnlyList<string> paths) => _model.ContextPaths = string.Join("\n", paths);

    private async Task SubmitAsync()
    {
        _busy = true;
        try
        {
            var projectId = FixedProjectId ?? (_model.OwnerType == "project" ? _model.ProjectId : null);
            var serverId = FixedProjectId.HasValue ? null : (_model.OwnerType == "server" ? _model.ServerId : null);
            var eventTypes = _model.EventTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            // Only a project task works on a repository: a server task sends no repository context.
            var contextPaths = projectId.HasValue
                ? _model.ContextPaths
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList()
                : [];
            var gitRange = projectId.HasValue && !string.IsNullOrWhiteSpace(_model.GitRange)
                ? _model.GitRange.Trim()
                : null;

            if (IsEdit)
            {
                var updated = await Api.Ai.UpdateAiTaskAsync(DefinitionId!.Value, new UpdateAiTaskDefinitionRequest
                {
                    Name = _model.Name,
                    ProfileId = _model.ProfileId,
                    PromptTemplate = _model.PromptTemplate,
                    ProjectId = projectId,
                    ServerId = serverId,
                    Schedule = string.IsNullOrWhiteSpace(_model.Schedule) ? null : _model.Schedule,
                    EventTypes = eventTypes,
                    ContextPaths = contextPaths,
                    GitRange = gitRange,
                    Enabled = _model.Enabled
                });
                if (updated is not null)
                {
                    Notify.Success("Updated");
                    Dialog.Close(true);
                }
            }
            else
            {
                var created = await Api.Ai.CreateAiTaskAsync(new CreateAiTaskDefinitionRequest
                {
                    Name = _model.Name,
                    ProfileId = _model.ProfileId,
                    PromptTemplate = _model.PromptTemplate,
                    ProjectId = projectId,
                    ServerId = serverId,
                    Schedule = string.IsNullOrWhiteSpace(_model.Schedule) ? null : _model.Schedule,
                    EventTypes = eventTypes,
                    ContextPaths = contextPaths,
                    GitRange = gitRange,
                    Enabled = _model.Enabled
                });
                if (created is not null)
                {
                    Notify.Success("Created");
                    Dialog.Close(true);
                }
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Cancel() => Dialog.Close(false);

    private sealed class EditModel
    {
        [Required, StringLength(120)]
        public string Name { get; set; } = string.Empty;

        public int ProfileId { get; set; }

        [Required, StringLength(20_000)]
        public string PromptTemplate { get; set; } = string.Empty;

        public string OwnerType { get; set; } = "server";
        public int? ProjectId { get; set; }
        public int? ServerId { get; set; }
        public string Schedule { get; set; } = string.Empty;
        public string EventTypes { get; set; } = string.Empty;

        /// <summary>One workspace-relative path per line.</summary>
        public string ContextPaths { get; set; } = string.Empty;

        [StringLength(200)]
        public string GitRange { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;
    }
}
