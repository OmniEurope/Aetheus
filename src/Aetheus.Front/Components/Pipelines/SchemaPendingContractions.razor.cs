// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The pending contractions of the database (recette R-18), as a grid of what to retire, the step and the earliest release.</summary>
public partial class SchemaPendingContractions
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PendingContractionDto> Contractions { get; set; } = default!;

    /// <summary><c>Table.Column</c> for one column, <c>Table.(A, B)</c> for several.</summary>
    internal static string Target(PendingContractionDto contraction) => contraction.Columns.Count == 1
        ? $"{contraction.Table}.{contraction.Columns[0]}"
        : $"{contraction.Table}.({string.Join(", ", contraction.Columns)})";

    internal static OmniTone StepTone(SchemaContractionStep step) =>
        step == SchemaContractionStep.Drop ? OmniTone.Warning : OmniTone.Info;

    private string PossibleFrom(PendingContractionDto contraction) => SchemaContractionMarkdown.PossibleFrom(contraction, L);

    private string Markdown => SchemaContractionMarkdown.Build(Contractions, L);
    private Task CopyMarkdownAsync() => Clipboard.CopyAsync(Markdown, L["SchemaContractionExportCopied"]);
    private Task DownloadMarkdownAsync() => Js.InvokeVoidAsync("downloadFile", "schema-contractions.md", Markdown, "text/markdown").AsTask();

    private string? AppliedHint(PendingContractionDto contraction) =>
        contraction.AppliedInRelease is { Length: > 0 } applied
            ? string.Format(L["SchemaPendingAppliedIn"].Value, contraction.RequiredAfterMigration, applied)
            : null;
}
