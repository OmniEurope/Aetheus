// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

public partial class ProjectPipelinesSection
{
    [Parameter, EditorRequired] public List<PipelineDto>? Pipelines { get; set; }
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    private bool _readinessLoaded;
    private int? _readinessProjectId;

    protected override void OnParametersSet()
    {
        if (_readinessProjectId == ProjectId) return;
        _readinessProjectId = ProjectId;
        _readinessLoaded = false;
    }

    private void OnReadinessLoaded() => _readinessLoaded = true;
}
