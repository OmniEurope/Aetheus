// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary> What deleting a project would remove, counted per kind, so the confirmation says it
/// before anything is deleted. <see cref="ActiveRunCount"/> above zero means the deletion is
/// refused until those runs end. </summary>
public sealed record ProjectDeletionPreviewDto
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public int ActiveRunCount { get; init; }

    /// <summary>Every kind with at least one item, in the order of <see cref="ProjectDeletionItemKind"/>.</summary>
    public List<ProjectDeletionItemDto> Items { get; init; } = [];

    /// <summary>Recette R-97: the servers the project deployed to, read from its libraries, environments and
    /// backup policies. What lives on each is asked to its agent afterwards (deletion discovery).</summary>
    public List<ProjectDeletionServerDto> Servers { get; init; } = [];

    /// <summary>Library values Aetheus will not use to find or remove anything: what they name may stay.</summary>
    public List<ProjectDeletionRefusedValueDto> RefusedValues { get; init; } = [];

    /// <summary>Recette R-51: set when the project is already deleted and waits for its server cleanup to be
    /// relaunched ("Suppression à terminer"); its name is then not asked again.</summary>
    public DateTime? DeletionRequestedAt { get; init; }
}

public sealed record ProjectDeletionItemDto(ProjectDeletionItemKind Kind, int Count);
