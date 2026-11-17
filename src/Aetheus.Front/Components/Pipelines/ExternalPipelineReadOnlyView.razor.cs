// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-11: the read-only replacement of a pipeline page's editing tabs for an imported pipeline.</summary>
public partial class ExternalPipelineReadOnlyView
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineDto Pipeline { get; set; } = default!;

    /// <summary>Whether the stored definition is listed under the notice (the edit and visual tabs show it).</summary>
    [Parameter] public bool ShowDefinition { get; set; }

    private string? CommitHref => Pipeline is { SourceRepositoryId: { } repositoryId, ExternalImportCommit: { } commit }
        ? $"/git-repositories/{repositoryId}?tab=commits&search={Uri.EscapeDataString(commit)}"
        : null;
}
