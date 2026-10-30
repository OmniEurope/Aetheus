// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public abstract class ProjectWritableSectionBase : ComponentBase, IDisposable
{
    [Inject] protected ApiClient Api { get; set; } = default!;
    [Inject] protected IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] protected OmniDialogService Dialog { get; set; } = default!;
    [Inject] protected PermissionService Permissions { get; set; } = default!;
    [Inject] protected NotifyHelper Toast { get; set; } = default!;
    [Inject] protected BusyVeil Veil { get; set; } = default!;

    [Parameter] public int ProjectId { get; set; }

    protected bool _loading = true;
    protected bool _canWrite;

    protected override void OnInitialized()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        RefreshCanWrite();
    }

    private int? _loadedProjectId;

    // A parent re-render that passes the same project does not load the section again.
    protected override Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId)
            return Task.CompletedTask;
        _loadedProjectId = ProjectId;
        return LoadAsync();
    }

    protected abstract Task LoadAsync();

    private void OnPermissionsChanged()
    {
        RefreshCanWrite();
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Project);

    public void Dispose() => Permissions.OnPermissionsChanged -= OnPermissionsChanged;
}
