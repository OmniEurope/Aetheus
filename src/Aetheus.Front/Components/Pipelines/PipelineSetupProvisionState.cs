// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary> The libraries and vaults the setup wizard created for a project. Held in a scoped
/// service (memory of this browser tab, never storage) so the list of what was created stays on the
/// summary after the readiness check no longer reports them missing.
/// </summary>
public sealed class PipelineSetupProvisionState
{
    private readonly Dictionary<int, List<ProvisionedElement>> _byProject = [];

    /// <summary>What was created for the project, in creation order; empty when nothing was.</summary>
    public IReadOnlyList<ProvisionedElement> For(int projectId) =>
        _byProject.TryGetValue(projectId, out var elements) ? elements : [];

    /// <summary>Adds what one provisioning created; an element already listed is not listed twice.</summary>
    public void Record(int projectId, PipelineRequirementsProvisionResultDto result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!_byProject.TryGetValue(projectId, out var elements))
        {
            elements = [];
            _byProject[projectId] = elements;
        }

        foreach (var (kind, created) in result.Libraries.Select(item => (ProvisionedKind.Library, item))
                     .Concat(result.Vaults.Select(item => (ProvisionedKind.Vault, item))))
        {
            if (elements.Exists(element => element.Kind == kind && element.Id == created.Id)) continue;
            elements.Add(new ProvisionedElement(kind, created.Id, created.Name, created.Keys.Count,
                created.KeysCopiedFrom is not null, created.FilledKeys.Count));
        }
    }

    /// <summary>Forgets the project's list once its pipelines are created.</summary>
    public void Clear(int projectId)
    {
        _byProject.Remove(projectId);
    }
}
