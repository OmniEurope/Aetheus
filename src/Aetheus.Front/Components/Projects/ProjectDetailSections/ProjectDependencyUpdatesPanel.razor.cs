// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Shared.Components.DependencyUpdates;

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

/// <summary>PLAN-007: reads the automatic dependency updates of the project once, when the Quality page opens.</summary>
public partial class ProjectDependencyUpdatesPanel
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private DependencyUpdatesOverviewDto _overview = new();
    private bool _loading = true;
    private bool _failed;
    private int? _loadedProjectId;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProjectId == ProjectId) return;
        _loadedProjectId = ProjectId;
        _loading = true;
        try
        {
            _overview = await Api.Analysis.GetDependencyUpdatesAsync(ProjectId);
            _failed = false;
        }
        catch (HttpRequestException)
        {
            _failed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    internal static OmniTone StatusTone(DependencyUpdateStatus status) => status switch
    {
        DependencyUpdateStatus.BranchPushed => OmniTone.Success,
        DependencyUpdateStatus.Failed => OmniTone.Danger,
        DependencyUpdateStatus.LockRefreshing => OmniTone.Info,
        _ => OmniTone.Warning
    };

    private string StatusText(DependencyUpdateStatus status) => L[$"DependencyUpdateStatus{status}"];

    private static OmniTone RunTone(PipelineStatus? status) => status is { } known ? PipelineHelper.GetRunBadge(known) : OmniTone.Neutral;

    private string RunText(DependencyUpdateRunDto run) =>
        run.Status is { } status ? L[$"DependencyUpdateRun{status}"] : L["DependencyUpdateRunNotFound"];
}
