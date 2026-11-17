// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the branch files and the fleet say about the templates' default commands (R-10 to R-13), read once per
/// project when the Capabilities step opens. <see cref="Value"/> is null while unknown: nothing is read, so nothing is claimed.</summary>
internal sealed class PipelineSetupDetectionReader(ApiClient api)
{
    private int? _projectId;

    /// <summary>What the repository holds; null while unknown.</summary>
    public PipelineSetupDetectionDto? Value { get; private set; }

    /// <summary>The branch that was read; null while unknown.</summary>
    public string? Branch { get; private set; }

    /// <summary>Forgets the reading, so the next <see cref="ReadAsync"/> reads again (another project, a mirror just synchronized).</summary>
    public void Forget()
    {
        Value = null;
        Branch = null;
        _projectId = null;
    }

    /// <summary>Reads the project's branch once; a project already read is not read again, and a failed read leaves nothing claimed.</summary>
    public async Task ReadAsync(int projectId)
    {
        if (_projectId == projectId) return;
        _projectId = projectId;
        try
        {
            var verdict = await api.Pipelines.CheckSetupReadinessAsync(projectId, PipelineSetupDetectionText.DetectedTemplates);
            Value = verdict?.Detection;
            Branch = verdict?.InspectedBranch;
        }
        catch (HttpRequestException)
        {
            // Nothing is read, so nothing is claimed: no warning, and the starting options stay.
            Forget();
        }
    }
}
