// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Projects;

/// <summary>R-47 (user decision 2026-11-03): says, on the project's overview, to finish the telemetry configuration for as long as
/// the server reports it pending (the wizard configured it and the stored repository inventory does not list the telemetry NuGet
/// package yet). The server decides; a refused or failed read shows nothing, never an invented warning.</summary>
public partial class ProjectTelemetrySetupWarning
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private bool _pending;
    private int? _loadedProjectId;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        _pending = false;
        try
        {
            var setup = await Api.Monitoring.GetTelemetrySetupAsync(ProjectId);
            if (_loadedProjectId == ProjectId) _pending = setup?.Pending ?? false;
        }
        catch (HttpRequestException)
        {
            _pending = false;
        }
    }
}
