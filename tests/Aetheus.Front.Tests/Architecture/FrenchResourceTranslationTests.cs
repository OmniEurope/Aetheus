// SPDX-License-Identifier: EUPL-1.2
using System.Xml.Linq;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Identical French and English resource values require the explicit same-language comment
/// when legitimate. Untranslated values without that justification fail.</summary>
public sealed class FrenchResourceTranslationTests
{
    private const string SameInBothLanguages = "same in both languages";

    [Fact]
    public void EveryFrenchValue_IsTranslated_OrMarkedAsTheSameInBothLanguages()
    {
        var english = Entries("AppStrings.resx");

        var untranslated = Entries("AppStrings.fr-FR.resx")
            .Where(pair => !pair.Value.MarkedIdentical
                && english.TryGetValue(pair.Key, out var source)
                && string.Equals(source.Value, pair.Value.Value, StringComparison.Ordinal))
            .Select(pair => $"{pair.Key} = \"{pair.Value.Value}\"")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(untranslated.Count == 0,
            $"French values identical to English (translate them, or add <comment>{SameInBothLanguages}</comment> to the French entry):\n"
            + string.Join('\n', untranslated));
    }

    [Fact]
    public void TheIdenticalMark_StaysOnlyOnValuesStillIdentical()
    {
        // A value translated since keeps no reason to stay marked: a mark that no longer matches would
        // silently pre-approve a future regression of that entry.
        var english = Entries("AppStrings.resx");

        var stale = Entries("AppStrings.fr-FR.resx")
            .Where(pair => pair.Value.MarkedIdentical
                && (!english.TryGetValue(pair.Key, out var source)
                    || !string.Equals(source.Value, pair.Value.Value, StringComparison.Ordinal)))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0, "Entries marked identical that no longer are: " + string.Join(", ", stale));
    }

    private static Dictionary<string, (string Value, bool MarkedIdentical)> Entries(string fileName) =>
        XDocument.Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", fileName))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => (
                    (string?)element.Element("value") ?? string.Empty,
                    string.Equals(((string?)element.Element("comment"))?.Trim(), SameInBothLanguages, StringComparison.Ordinal)),
                StringComparer.Ordinal);
}
