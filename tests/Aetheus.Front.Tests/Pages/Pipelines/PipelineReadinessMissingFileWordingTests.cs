// SPDX-License-Identifier: EUPL-1.2
using System.Xml.Linq;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.Architecture;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R3-61: the two findings about files a new project lacks once said only "missing,
/// commit them", which read as the user's fault and pointed to a single way out. They now name the
/// cause (the templates call files Aetheus does not create, absent from the checked branch) and what
/// to do, in both languages; for scripts, shell inside the pipeline comes first (ADR-050).</summary>
public sealed class PipelineReadinessMissingFileWordingTests
{
    [Theory]
    [InlineData("AppStrings.resx", "Cause:", "What to do:", "does not create")]
    [InlineData("AppStrings.fr-FR.resx", "Cause :", "À faire :", "ne les crée pas")]
    public void MissingAdapterScripts_SaysTheCauseAndWhatToDo(string file, string cause, string action, string notCreated)
    {
        var hint = Value(file, PipelineReadinessText.HintKey(PipelineSetupReadinessKind.MissingAdapterScripts));

        Assert.Contains(cause, hint, StringComparison.Ordinal);
        Assert.Contains(notCreated, hint, StringComparison.Ordinal);
        var todo = hint[hint.IndexOf(action, StringComparison.Ordinal)..];
        Assert.Contains("YAML", todo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AppStrings.resx", "Cause:", "What to do:", "--config")]
    [InlineData("AppStrings.fr-FR.resx", "Cause :", "À faire :", "--config")]
    public void MissingScannerConfiguration_SaysTheCauseAndWhatToDo(string file, string cause, string action, string option)
    {
        var hint = Value(file, PipelineReadinessText.HintKey(PipelineSetupReadinessKind.MissingScannerConfiguration));

        Assert.Contains(cause, hint, StringComparison.Ordinal);
        Assert.Contains(option, hint, StringComparison.Ordinal);
        Assert.Contains(action, hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AppStrings.resx", "adapter")]
    [InlineData("AppStrings.fr-FR.resx", "adaptateur")]
    public void MissingAdapterScripts_TitleDoesNotUseTheInternalWord(string file, string jargon)
    {
        var title = Value(file, PipelineReadinessText.TitleKey(PipelineSetupReadinessKind.MissingAdapterScripts));

        Assert.DoesNotContain(jargon, title, StringComparison.OrdinalIgnoreCase);
    }

    private static string Value(string file, string key) =>
        (string?)XDocument.Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", file))
            .Root!
            .Elements("data")
            .Single(element => (string?)element.Attribute("name") == key)
            .Element("value")
        ?? string.Empty;
}
