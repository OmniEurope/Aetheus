// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectServersSection : ProjectPagedSectionBase<ProjectServerDto>
{
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    /// <summary>The shared project detail: its server count feeds the overview checklist, so an
    /// attach or a detach here must refresh it, or the "attach a server" step stays "to do" until
    /// the page is reloaded.</summary>
    [Inject] private ProjectDetailLoader Loader { get; set; } = default!;

    private List<ProjectServerDto>? _servers;
    private List<ServerDto>? _agentServers;
    private int _totalCount;
    protected override string DefaultSortBy => "DisplayName";

    // Recette R-212: the Type header filter is a checkable list of every project server type.
    private Func<string, string>? _typeText;
    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<ProjectServerType>(L);

    protected override void ResetForProject() => _agentServers = null;

    protected override async Task LoadAsync()
    {
        var projectId = ProjectId;
        _loading = true;
        try
        {
            var result = await ReadPageAsync(projectId, _page, _pageSize, _sortBy, _sortDescending, _columnFilters);
            if (ProjectId != projectId) return;
            _servers = result.Items;
            _totalCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            if (ProjectId != projectId) return;
            _servers = [];
            _totalCount = 0;
        }
        finally { if (ProjectId == projectId) _loading = false; }
    }

    protected override Task<PaginatedResult<ProjectServerDto>> ReadPageAsync(
        int projectId, int page, int pageSize, string sortBy, bool sortDescending, IReadOnlyList<GridFilter> filters) =>
        Api.Projects.GetProjectServersPageAsync(projectId, page, pageSize, sortBy, sortDescending, filters: filters);

    private void OnRowClick(ProjectServerDto server)
    {
        if (server.Type == ProjectServerType.AgentServer && server.ServerId.HasValue)
            Nav.NavigateTo($"/servers/{server.ServerId}/overview");
    }

    private async Task EnsureAgentServersAsync()
    {
        if (_agentServers is not null) return;
        var projectId = ProjectId;
        var servers = await Api.Servers.GetAllServersAsync();
        if (ProjectId == projectId) _agentServers = servers;
    }

    private async Task OpenAddDialogAsync()
    {
        await EnsureAgentServersAsync();
        var result = await Dialog.OpenAsync<ProjectServerDialog>(
            L["AddProjectServer"],
            new Dictionary<string, object?>
            {
                { "AgentServers", _agentServers }
            },
            new OmniDialogOptions { Width = "32rem", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

        if (result is not ProjectServerDialogResult data) return;

        var created = await Api.Projects.CreateProjectServerAsync(ProjectId, new CreateProjectServerRequest
        {
            Type = data.Type,
            ServerId = data.Type == ProjectServerType.AgentServer ? data.ServerId : null,
            DisplayName = data.DisplayName,
            Host = data.Host,
            Port = data.Port,
            Notes = data.Notes
        });
        if (created is not null)
        {
            await LoadAsync();
            await Loader.ForceReloadAsync(ProjectId);
            Toast.Success("Created", "ProjectServerCreated");
        }
    }

    private async Task OpenEditDialogAsync(ProjectServerDto server)
    {
        var result = await Dialog.OpenAsync<ProjectServerDialog>(
            L["EditProjectServer"],
            new Dictionary<string, object?>
            {
                { "Editing", server },
                { "DisplayName", server.DisplayName },
                { "Host", server.Host },
                { "Port", server.Port },
                { "Notes", server.Notes },
                { "Type", server.Type }
            },
            new OmniDialogOptions { Width = "32rem", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

        if (result is not ProjectServerDialogResult data) return;

        var updated = await Api.Projects.UpdateProjectServerAsync(ProjectId, server.Id, new UpdateProjectServerRequest
        {
            DisplayName = data.DisplayName,
            Host = data.Host,
            Port = data.Port,
            Notes = data.Notes
        });
        if (updated is not null)
        {
            await LoadAsync();
            await Loader.ForceReloadAsync(ProjectId);
            Toast.Success("Updated", "ProjectServerUpdated");
        }
    }

    private async Task DeleteServerAsync(ProjectServerDto server)
    {
        var confirmed = await Dialog.Confirm(L["DeleteConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var success = await Api.Projects.DeleteProjectServerAsync(ProjectId, server.Id);
        if (success)
        {
            await LoadAsync();
            Toast.Success("Deleted", "Deleted");
        }
        else
        {
            Toast.Error("Error", "DeleteFailed");
        }
    }

    private static OmniTone GetTypeBadge(ProjectServerType type) => type switch
    {
        ProjectServerType.AgentServer => OmniTone.Accent,
        ProjectServerType.ExternalHost => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    private static OmniTone GetStatusBadge(ServerStatus? status) => status switch
    {
        ServerStatus.Online => OmniTone.Success,
        ServerStatus.Offline => OmniTone.Danger,
        _ => OmniTone.Neutral
    };


    /// <summary>Recette R2-124: the export writes the state the badge shows.</summary>
    private Func<ProjectServerDto, object?> StatusExport => _statusExport ??= server =>
        server.Type == ProjectServerType.AgentServer && server.ServerStatus.HasValue
            ? L[server.ServerStatus.Value.ToString()].Value
            : L["External"].Value;
    private Func<ProjectServerDto, object?>? _statusExport;
}
