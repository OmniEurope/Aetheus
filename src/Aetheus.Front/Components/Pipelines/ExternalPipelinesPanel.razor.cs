// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-11 (decision of 2026-11-10, option A): lists the <c>.pipeline/*.yaml</c> files of the project's external
/// repository mirrors with their state, and imports one read-only at the commit shown. The import button follows the
/// project WRITE permission; the backend checks it again, the button is only a convenience.
/// </summary>
public partial class ExternalPipelinesPanel : IDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>Raised after an import changed the project's pipelines, so the host can reload its list.</summary>
    [Parameter] public EventCallback Imported { get; set; }

    private ExternalPipelineCandidatesDto? _candidates;
    private bool _canWrite;
    private string? _importing;
    private string? _error;
    private int? _loadedProjectId;

    protected override void OnInitialized()
    {
        Permissions.OnPermissionsChanged += OnPermissionsChanged;
        _canWrite = Permissions.CanWrite(ResourceType.Project);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _candidates = await Api.ExternalPipelines.GetCandidatesAsync(ProjectId);
        }
        catch (HttpRequestException)
        {
            // The list of the project's own pipelines stays usable when the mirrors cannot be read.
            _candidates = null;
        }
    }

    internal async Task ImportAsync(ExternalPipelineCandidateDto item)
    {
        _importing = item.FilePath;
        _error = null;
        var outcome = await Api.ExternalPipelines.ImportAsync(ProjectId, new ImportExternalPipelineRequest
        {
            RepositoryId = item.RepositoryId,
            FilePath = item.FilePath,
            CommitSha = item.CommitSha
        });
        _importing = null;
        if (!outcome.IsSuccess)
        {
            _error = L["ExternalPipelinesImportFailed", outcome.Error?.Message ?? string.Empty];
            await LoadAsync();
            return;
        }
        Toast.Success("ExternalPipelinesImportedTitle", "ExternalPipelinesImportedMessage", item.Name);
        await LoadAsync();
        await Imported.InvokeAsync();
    }

    private static string CommitHref(ExternalPipelineCandidateDto item) =>
        $"/git-repositories/{item.RepositoryId}?tab=commits&search={Uri.EscapeDataString(item.CommitSha)}";

    private void OnPermissionsChanged()
    {
        _canWrite = Permissions.CanWrite(ResourceType.Project);
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Permissions.OnPermissionsChanged -= OnPermissionsChanged;
        GC.SuppressFinalize(this);
    }
}
