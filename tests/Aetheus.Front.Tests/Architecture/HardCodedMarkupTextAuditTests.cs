// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// R-113 (rule 6: everything the user reads is French). A sentence typed straight into Razor markup, as element
/// text or as a display attribute (title, label, tooltip, help...), never reaches the localizer: it is shown
/// in the same language to every user. The visible text of a page goes through <c>L["Key"]</c>.
///
/// The scan reads each element whose whole content is plain text (no <c>@</c> expression, no markup inside) and
/// each display attribute whose whole value is plain text, and refuses it when it reads as a sentence or a
/// label: two words or more, one of them an ordinary word of three letters or more. A single token (a product or
/// protocol name such as NuGet or YAML, an acronym, an identifier) is not a sentence and is left to review, as
/// are <c>code</c> and <c>pre</c> elements and the example values of a <c>Placeholder</c>.
/// </summary>
public sealed class HardCodedMarkupTextAuditTests
{
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ScriptOrStyle = new(
        @"<(?<tag>script|style)\b.*?</\k<tag>>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // <tag attributes>text</tag>, where the attributes may hold quoted ">" and the text holds no markup and no expression.
    private static readonly Regex TextElement = new(
        "<(?<tag>[A-Za-z][\\w.]*)(?:[^<>\"]|\"[^\"]*\")*?(?<!/)>(?<text>[^<>@{}]+)</\\k<tag>>",
        RegexOptions.Compiled);

    private static readonly Regex DisplayAttribute = new(
        "(?<![\\w@:-])(?<name>Title|Text|Label|Description|Help|Tooltip|Caption|Subtitle|Message|Legend|Header|EmptyText|AriaLabel|aria-label|title|alt)=\"(?<value>[^\"@{}]+)\"",
        RegexOptions.Compiled);

    private static readonly Regex OrdinaryWord = new(@"^[A-Za-zÀ-ÿ]{3,}[.,;:!?]?$", RegexOptions.Compiled);

    private static readonly HashSet<string> LiteralTextTags = new(StringComparer.OrdinalIgnoreCase) { "code", "pre" };

    [Fact]
    public void NoRazorMarkup_WritesASentenceOrALabelOutsideTheLocalizer()
    {
        var root = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var offenders = new List<string>();
        var files = RepositoryScan.Enumerate(root, "*.razor");

        foreach (var file in files)
            offenders.AddRange(Offenders(File.ReadAllText(file)).Select(offender =>
                $"{Path.GetRelativePath(RepositoryScan.Root, file)}: {offender}"));

        Assert.True(files.Count > 100, $"Only {files.Count} Razor files were read: the scan is broken.");
        Assert.True(offenders.Count == 0,
            "Visible text written in the markup instead of the localizer (add a key to AppStrings.resx and AppStrings.fr-FR.resx, read it with L[\"Key\"]):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheDetector_FlagsASentenceAndALabel_ButNotAProductNameOrAnExpression()
    {
        Assert.Single(Offenders("<OmniText>Save the changes now</OmniText>"));
        Assert.Single(Offenders("<OmniButton Title=\"Open the page\">x</OmniButton>"));
        Assert.Single(Offenders("<span>Not available</span>"));
        Assert.Empty(Offenders("<OmniHeading>NuGet</OmniHeading>"));
        Assert.Empty(Offenders("<OmniBadge Title=\"YAML\">x</OmniBadge>"));
        Assert.Empty(Offenders("<span>@L[\"Key\"] and more</span>"));
        Assert.Empty(Offenders("<code>dotnet nuget push</code>"));
        Assert.Empty(Offenders("@* <span>Some commented sentence</span> *@"));
        Assert.Empty(Offenders("<OmniTextBox Placeholder=\"example of a value\" />"));
    }

    private static IEnumerable<string> Offenders(string markup)
    {
        var text = ScriptOrStyle.Replace(RazorComment.Replace(markup, string.Empty), string.Empty);
        foreach (Match element in TextElement.Matches(text))
        {
            if (LiteralTextTags.Contains(element.Groups["tag"].Value)) continue;
            if (ReadsAsASentence(element.Groups["text"].Value))
                yield return $"<{element.Groups["tag"].Value}> {Shorten(element.Groups["text"].Value)}";
        }

        foreach (Match attribute in DisplayAttribute.Matches(text))
            if (ReadsAsASentence(attribute.Groups["value"].Value))
                yield return $"{attribute.Groups["name"].Value}=\"{Shorten(attribute.Groups["value"].Value)}\"";
    }

    private static bool ReadsAsASentence(string value)
    {
        var tokens = WebUtility.HtmlDecode(value)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Any(char.IsLetter))
            .ToList();
        return tokens.Count >= 2 && tokens.Any(token => OrdinaryWord.IsMatch(token));
    }

    private static string Shorten(string value)
    {
        var line = Regex.Replace(value, @"\s+", " ").Trim();
        return line.Length <= 80 ? line : line[..80] + "...";
    }
}
