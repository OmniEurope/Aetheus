// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-26: what the last preparation of a project's collection did, held in memory of this browser tab (never storage) so
/// the overview, where the wizard ends, can tell it item by item until it is dismissed.</summary>
public sealed class PipelineSetupCollectionState
{
    private readonly Dictionary<int, PipelineSetupCollectionResultDto> _byProject = [];

    /// <summary>The preparation of the project that was not dismissed; null when there is none.</summary>
    public PipelineSetupCollectionResultDto? For(int projectId) =>
        _byProject.TryGetValue(projectId, out var result) ? result : null;

    public void Record(int projectId, PipelineSetupCollectionResultDto result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Items.Count > 0) _byProject[projectId] = result;
    }

    public void Dismiss(int projectId) => _byProject.Remove(projectId);
}
