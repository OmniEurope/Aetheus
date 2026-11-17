// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The Markdown export of the expand/contract tracking (recette R-66), written to be pasted to an AI: for each
/// contraction the table, the columns, the step, the migration that delivered it, the condition "possible from
/// release X" and a sentence saying what is to be done and by whom. Built from the back's registry, never hand-kept.
/// </summary>
internal static class SchemaContractionMarkdown
{
    public static string Build(IReadOnlyList<PendingContractionDto> contractions, IStringLocalizer<AppStrings> localizer)
    {
        var md = new StringBuilder()
            .AppendLine($"# {localizer["SchemaContractionExportTitle"]}")
            .AppendLine()
            .AppendLine(localizer["SchemaContractionExportIntro"])
            .AppendLine();
        if (contractions.Count == 0)
            return md.AppendLine(localizer["SchemaNoPendingContractions"]).ToString();

        foreach (var contraction in contractions)
        {
            var columns = string.Join(", ", contraction.Columns.Select(column => $"`{column}`"));
            md.AppendLine($"## {contraction.Table}")
                .AppendLine($"- {localizer["SchemaContractionExportTable"]} : `{contraction.Table}`")
                .AppendLine($"- {localizer["SchemaContractionExportColumns"]} : {columns}")
                .AppendLine($"- {localizer["SchemaPendingStep"]} : {localizer.Localize(contraction.Step)}")
                .AppendLine($"- {localizer["SchemaContractionExportMigration"]} : `{contraction.ExpandMigration}`")
                .AppendLine($"- {localizer["SchemaPendingPossibleFrom"]} : {PossibleFrom(contraction, localizer)}")
                .AppendLine($"- {localizer["SchemaContractionExportTodo"]} : {localizer[contraction.Step == SchemaContractionStep.Drop ? "SchemaContractionExportTodoDrop" : "SchemaContractionExportTodoUnmap"]}")
                .AppendLine();
        }
        return md.ToString();
    }

    /// <summary>The release when a deployment recorded the step it waits for; otherwise what it waits for, in words.</summary>
    internal static string PossibleFrom(PendingContractionDto contraction, IStringLocalizer<AppStrings> localizer)
    {
        if (contraction.PossibleFromRelease is { Length: > 0 } release) return release;
        return contraction.RequiredAfterMigration is { } migration
            ? string.Format(localizer["SchemaPendingAfterRelease"].Value, migration)
            : localizer["SchemaPendingAfterPreviousStep"];
    }
}
