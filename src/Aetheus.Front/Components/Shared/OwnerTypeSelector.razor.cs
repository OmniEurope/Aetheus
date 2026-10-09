// SPDX-License-Identifier: EUPL-1.2

using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public partial class OwnerTypeSelector
{
    [Parameter] public int? ProjectId { get; set; }
    [Parameter] public EventCallback<int?> ProjectIdChanged { get; set; }

    [Parameter] public int? EnvironmentId { get; set; }
    [Parameter] public EventCallback<int?> EnvironmentIdChanged { get; set; }

    [Parameter] public int? ProjectServerId { get; set; }
    [Parameter] public EventCallback<int?> ProjectServerIdChanged { get; set; }

    /// <summary>Pre-loaded project list passed by the parent form.</summary>
    [Parameter, EditorRequired] public List<ProjectDto> Projects { get; set; } = [];

    private OwnerKind _kind = OwnerKind.Global;
    private readonly string _radioGroupName = $"owner-kind-{Guid.NewGuid():N}";
    private bool _userChoseKind;
    private int? _selectedProjectId;
    private List<EnvironmentDto> _environments = [];
    private List<ProjectServerDto> _projectServers = [];
    // FRONT-021: the project each list was last loaded for. An empty answer is a loaded list, not a
    // missing one: gating the reload on Count == 0 re-fetched it after every render, without end.
    private int? _environmentsLoadedFor;
    private int? _projectServersLoadedFor;

    private IReadOnlyList<OmniOption<int?>> ProjectOptions =>
        Projects.Select(project => new OmniOption<int?>(project.Id,
            project.Name ?? throw new InvalidOperationException("Project has no name"))).ToArray();

    private IReadOnlyList<OmniOption<int?>> EnvironmentOptions =>
        _environments.Select(environment => new OmniOption<int?>(environment.Id,
            environment.Name ?? throw new InvalidOperationException("Environment has no name"))).ToArray();

    private IReadOnlyList<OmniOption<int?>> ServerOptions =>
        _projectServers.Select(server => new OmniOption<int?>(server.Id, ServerOptionText(server))).ToArray();

    private static string ServerOptionText(ProjectServerDto server) =>
        !string.IsNullOrWhiteSpace(server.ServerName) ? server.ServerName
        : !string.IsNullOrWhiteSpace(server.DisplayName) ? server.DisplayName
        : throw new InvalidOperationException("Project server has no name");

    protected override async Task OnParametersSetAsync()
    {
        // Bound owner ids define the mode until the user picks one. Afterwards the user's choice
        // must survive the parent re-render caused by SetOwnership(null, null, null): re-inferring
        // on every render immediately snapped Project/Environment/Server back to Global before the
        // user could choose the scoped owner.
        //
        // What this must NOT do is latch on the first parameter set regardless of content. Owners
        // arrive late: PipelineEdit assigns _model.ProjectId inside an async load that completes
        // after the first render, so latching on render one inferred Global from a still-null id and
        // never looked again - opening /pipelines/new?projectId=13 from a project offered "Global"
        // with no project dropdown. Latching on the user's choice instead of on the first render
        // keeps the protection above and lets a late-arriving owner still be honoured.
        if (_userChoseKind) return;

        // Infer the kind from the bound values
        if (ProjectServerId is > 0)
        {
            _kind = OwnerKind.ProjectServer;
            // Find the project that owns this ProjectServer
            _selectedProjectId = await FindProjectForServerAsync(ProjectServerId.Value);
        }
        else if (EnvironmentId is > 0)
        {
            _kind = OwnerKind.Environment;
            _selectedProjectId = await FindProjectForEnvironmentAsync(EnvironmentId.Value);
        }
        else if (ProjectId is > 0)
        {
            _kind = OwnerKind.Project;
            _selectedProjectId = ProjectId;
        }
        else
        {
            _kind = OwnerKind.Global;
        }
    }

    // Not gated on firstRender: the owner the mode was inferred from can arrive after it, and its
    // dropdown would then render permanently empty. Gated on the list not yet being loaded for the
    // selected project instead, which is idempotent across the re-renders that follow, empty lists
    // included.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_selectedProjectId is not > 0) return;

        if (_kind == OwnerKind.Environment && _environmentsLoadedFor != _selectedProjectId)
            await LoadEnvironmentsAsync(_selectedProjectId.Value);
        else if (_kind == OwnerKind.ProjectServer && _projectServersLoadedFor != _selectedProjectId)
            await LoadProjectServersAsync(_selectedProjectId.Value);
        else
            return;

        StateHasChanged();
    }

    private async Task OnKindChanged(OwnerKind newKind)
    {
        // From here the user owns the mode: never re-infer it from the bound ids, which
        // SetOwnership below is about to clear.
        _userChoseKind = true;
        _kind = newKind;
        _selectedProjectId = null;
        _environments = [];
        _projectServers = [];
        _environmentsLoadedFor = null;
        _projectServersLoadedFor = null;
        await SetOwnership(null, null, null);
    }

    private async Task OnProjectChanged(object? value)
    {
        _selectedProjectId = (int?)value;
        _environments = [];
        _projectServers = [];
        _environmentsLoadedFor = null;
        _projectServersLoadedFor = null;

        if (_kind == OwnerKind.Project)
        {
            await SetOwnership(_selectedProjectId, null, null);
        }
        else if (_kind == OwnerKind.Environment && _selectedProjectId is > 0)
        {
            await LoadEnvironmentsAsync(_selectedProjectId.Value);
            await SetOwnership(null, null, null);
        }
        else if (_kind == OwnerKind.ProjectServer && _selectedProjectId is > 0)
        {
            await LoadProjectServersAsync(_selectedProjectId.Value);
            await SetOwnership(null, null, null);
        }
    }

    private async Task OnEnvironmentChanged(object? value)
    {
        await SetOwnership(null, (int?)value, null);
    }

    private async Task OnProjectServerChanged(object? value)
    {
        await SetOwnership(null, null, (int?)value);
    }

    private async Task SetOwnership(int? projectId, int? environmentId, int? projectServerId)
    {
        await ProjectIdChanged.InvokeAsync(projectId);
        await EnvironmentIdChanged.InvokeAsync(environmentId);
        await ProjectServerIdChanged.InvokeAsync(projectServerId);
    }

    private async Task LoadEnvironmentsAsync(int projectId)
    {
        _environments = await Api.Servers.GetAllEnvironmentsAsync(projectId);
        _environmentsLoadedFor = projectId;
    }

    private async Task LoadProjectServersAsync(int projectId)
    {
        _projectServers = await Api.Projects.GetProjectServersAsync(projectId);
        _projectServersLoadedFor = projectId;
    }

    private async Task<int?> FindProjectForServerAsync(int projectServerId)
    {
        var lookups = Projects.Select(async project =>
        {
            try
            {
                var servers = await Api.Projects.GetProjectServersAsync(project.Id);
                return servers.Any(server => server.Id == projectServerId) ? project.Id : (int?)null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        });
        return (await Task.WhenAll(lookups)).FirstOrDefault(projectId => projectId is not null);
    }

    private async Task<int?> FindProjectForEnvironmentAsync(int environmentId)
    {
        try
        {
            return (await Api.Servers.GetEnvironmentAsync(environmentId))?.ProjectId;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public enum OwnerKind { Global, Project, Environment, ProjectServer }
}
