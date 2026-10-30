// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Tests.Architecture;

namespace Aetheus.Back.Tests;

/// <summary>
/// The template editor shows each variable by <c>MailVariable_{Name}</c> and its help by
/// <c>MailVariable_{Name}_Help</c>; a missing entry shows the raw key to the user.
/// </summary>
public sealed class MailVariableTranslationTests
{
    [Theory]
    [InlineData("AppStrings.resx")]
    [InlineData("AppStrings.fr-FR.resx")]
    public void EveryVariableOfEveryKind_HasALabelAndAHelp_InEachLanguage(string resx)
    {
        var keys = ReadKeys(resx);
        var missing = MailTemplateCatalog.Kinds
            .SelectMany(MailTemplateCatalog.VariableNames)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => new[] { "MailVariable_" + name, "MailVariable_" + name + "_Help" })
            .Where(key => !keys.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0, $"{resx} lacks: {string.Join(", ", missing)}");
    }

    private static HashSet<string> ReadKeys(string fileName) =>
        Regex.Matches(
                File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", fileName)),
                """<data name="(?<key>\w+)"[^>]*>\s*<value>[^<]+</value>""")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
}
