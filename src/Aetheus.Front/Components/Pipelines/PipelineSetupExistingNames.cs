// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-38: the names of the pipelines the project already holds. A name is unique within its project, so creating
/// <c>oe1-security</c> in a project that has it (an external-security pipeline from an earlier wizard run) failed on the
/// server's unique index and the wizard only said "Operation failed". The wizard reads the names when its summary opens,
/// says which pipelines it keeps, and creates the others. Nothing is replaced: an existing pipeline has runs and its own
/// definition. A read that fails claims nothing: the server still refuses a duplicate, now with its own sentence.
/// </summary>
internal sealed class PipelineSetupExistingNames(ApiClient api)
{
    /// <summary>The names the project's pipelines are listed with; the largest page the API serves.</summary>
    private const int PageSize = 200;

    private HashSet<string> _names = new(StringComparer.Ordinal);
    private int? _projectId;

    /// <summary>The project already holds a pipeline of this name.</summary>
    public bool Has(string name) => _names.Contains(name);

    /// <summary>R-70: every name read, for the pipelines the wizard writes a reference to.</summary>
    public IReadOnlySet<string> Names => _names;

    /// <summary>Reads the project's pipeline names; again for another project, once for the same one.</summary>
    public async Task LoadAsync(int projectId)
    {
        if (_projectId == projectId) return;
        try
        {
            var page = await api.Pipelines.GetPipelinesAsync(1, PageSize, projectId: projectId);
            _names = new HashSet<string>(page.Items.Select(pipeline => pipeline.Name), StringComparer.Ordinal);
            _projectId = projectId;
        }
        catch (Exception exception) when (RunParametersReading.IsTransportFailure(exception))
        {
            // Nothing is read, so nothing is claimed.
            _names = new HashSet<string>(StringComparer.Ordinal);
            _projectId = null;
        }
    }

    /// <summary>Forgets the reading, so the next summary reads again (pipelines may have been created or deleted since).</summary>
    public void Forget() => _projectId = null;
}
