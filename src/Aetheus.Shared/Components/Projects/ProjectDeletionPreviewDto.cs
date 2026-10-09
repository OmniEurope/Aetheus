// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>
/// Recette R2-122: what deleting a project would remove, counted per kind, so the confirmation says
/// it before anything is deleted. <see cref="ActiveRunCount"/> above zero means the deletion is
/// refused until those runs end.
/// </summary>
public sealed record ProjectDeletionPreviewDto
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public int ActiveRunCount { get; init; }

    /// <summary>Every kind with at least one item, in the order of <see cref="ProjectDeletionItemKind"/>.</summary>
    public List<ProjectDeletionItemDto> Items { get; init; } = [];
}

public sealed record ProjectDeletionItemDto(ProjectDeletionItemKind Kind, int Count);
