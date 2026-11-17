// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Resources;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>Recette R-66: the Markdown export of the expand/contract tracking, meant to be pasted to an AI.</summary>
public sealed class SchemaContractionMarkdownTests
{
    private sealed class KeyLocalizer : IStringLocalizer<AppStrings>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name + ":" + string.Join("|", arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private static readonly PendingContractionDto[] Sample =
    [
        new() { Id = "a", Table = "Servers", Columns = ["OldA", "OldB"], Step = SchemaContractionStep.Unmap, ExpandMigration = "20260924_Expand", RequiredAfterMigration = null },
        new() { Id = "b", Table = "VaultSecretVersions", Columns = ["EncryptedValue"], Step = SchemaContractionStep.Drop, ExpandMigration = "20260924_Expand", RequiredAfterMigration = "20261010_Unmap", AppliedInRelease = "1.5.30", PossibleFromRelease = "1.5.31" }
    ];

    [Fact]
    public void EachLine_CarriesTableColumnsStepMigrationConditionAndWhatToDo()
    {
        var md = SchemaContractionMarkdown.Build(Sample, new KeyLocalizer());

        Assert.StartsWith("# SchemaContractionExportTitle", md, StringComparison.Ordinal);
        Assert.Contains("Servers", md, StringComparison.Ordinal);
        Assert.Contains("`OldA`, `OldB`", md, StringComparison.Ordinal);
        Assert.Contains("VaultSecretVersions", md, StringComparison.Ordinal);
        Assert.Contains("`EncryptedValue`", md, StringComparison.Ordinal);
        Assert.Contains("Enum_SchemaContractionStep_Unmap", md, StringComparison.Ordinal);
        Assert.Contains("Enum_SchemaContractionStep_Drop", md, StringComparison.Ordinal);
        Assert.Contains("`20260924_Expand`", md, StringComparison.Ordinal);
        Assert.Contains("1.5.31", md, StringComparison.Ordinal);
        Assert.Contains("SchemaPendingAfterPreviousStep", md, StringComparison.Ordinal);
        Assert.Contains("SchemaContractionExportTodoUnmap", md, StringComparison.Ordinal);
        Assert.Contains("SchemaContractionExportTodoDrop", md, StringComparison.Ordinal);
        Assert.Contains("SchemaContractionExportIntro", md, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutContractions_SaysSoInsteadOfAnEmptyList()
    {
        var md = SchemaContractionMarkdown.Build([], new KeyLocalizer());

        Assert.Contains("SchemaNoPendingContractions", md, StringComparison.Ordinal);
        Assert.DoesNotContain("SchemaContractionExportTodo", md, StringComparison.Ordinal);
    }
}
