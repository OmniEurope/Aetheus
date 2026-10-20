// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.AiTasks;

/// <summary>A folder or a file of the repository tree shown by <see cref="AiContextTreeNodes"/>.</summary>
public sealed class AiContextTreeNode(GitLightTreeEntryDto entry)
{
    public string Name { get; } = entry.Name;
    public string Path { get; } = entry.Path;
    public bool IsFolder { get; } = entry.Type == GitTreeEntryType.Tree;

    /// <summary>The value the tree selects: a folder keeps a trailing slash in the stored path.</summary>
    public string Value => IsFolder ? Path.TrimEnd('/') + "/" : Path;

    public List<AiContextTreeNode> Children { get; } = [];
}

public partial class AiContextTreeNodes
{
    [Parameter, EditorRequired] public IReadOnlyList<AiContextTreeNode> Nodes { get; set; } = [];

    [Parameter, EditorRequired] public Func<AiContextTreeNode, CancellationToken, Task> LoadChildren { get; set; } = default!;
}
