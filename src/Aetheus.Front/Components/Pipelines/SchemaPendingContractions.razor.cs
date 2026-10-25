// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The pending contractions of the database (recette R-18), as a grid of what to retire, the step and the earliest release.</summary>
public partial class SchemaPendingContractions
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PendingContractionDto> Contractions { get; set; } = default!;

    /// <summary><c>Table.Column</c> for one column, <c>Table.(A, B)</c> for several.</summary>
    internal static string Target(PendingContractionDto contraction) => contraction.Columns.Count == 1
        ? $"{contraction.Table}.{contraction.Columns[0]}"
        : $"{contraction.Table}.({string.Join(", ", contraction.Columns)})";

    internal static OmniTone StepTone(SchemaContractionStep step) =>
        step == SchemaContractionStep.Drop ? OmniTone.Warning : OmniTone.Info;

    /// <summary>The release when a deployment recorded the step it waits for; otherwise what it waits for, in words.</summary>
    private string PossibleFrom(PendingContractionDto contraction)
    {
        if (contraction.PossibleFromRelease is { Length: > 0 } release) return release;
        return contraction.RequiredAfterMigration is { } migration
            ? string.Format(L["SchemaPendingAfterRelease"].Value, migration)
            : L["SchemaPendingAfterPreviousStep"];
    }

    private string? AppliedHint(PendingContractionDto contraction) =>
        contraction.AppliedInRelease is { Length: > 0 } applied
            ? string.Format(L["SchemaPendingAppliedIn"].Value, contraction.RequiredAfterMigration, applied)
            : null;
}
