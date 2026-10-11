// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R1-26: the libraries and vaults the setup wizard created for a project, and which of them
/// the user has opened to check. Held in a scoped service (memory of this browser tab, never storage)
/// so the list survives the user leaving the wizard to open one and coming back to its summary.
/// </summary>
public sealed class PipelineSetupProvisionState
{
    private readonly Dictionary<int, List<ProvisionedElement>> _byProject = [];

    /// <summary>What was created for the project, in creation order; empty when nothing was.</summary>
    public IReadOnlyList<ProvisionedElement> For(int projectId) =>
        _byProject.TryGetValue(projectId, out var elements) ? elements : [];

    /// <summary>Adds what one provisioning created; an element already listed keeps its checked state.</summary>
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
                created.KeysCopiedFrom is not null));
        }
    }

    /// <summary>The user opened the element to check it.</summary>
    public void MarkChecked(int projectId, ProvisionedKind kind, int id)
    {
        if (For(projectId).FirstOrDefault(element => element.Kind == kind && element.Id == id) is { } element)
            element.Checked = true;
    }

    /// <summary>True when every created element was opened, or when nothing was created.</summary>
    public bool AllChecked(int projectId) => For(projectId).All(element => element.Checked);

    /// <summary>Forgets the project's list once its pipelines are created.</summary>
    public void Clear(int projectId) => _byProject.Remove(projectId);
}
