// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Guards the icon, accessibility and compact table-action contracts of OE buttons.</summary>
public class OmniButtonIconAuditTests
{
    private static readonly string[] DeprecatedIconSuffixes =
        ["_outline", "_outlined", "_rounded", "_sharp", "_two_tone", "_twotone"];

    private static readonly IReadOnlyDictionary<string, string> CanonicalTableActionIcons =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Copy"] = "OmniIconName.Copy",
            ["Delete"] = "OmniIconName.Delete",
            ["Edit"] = "OmniIconName.Edit",
            ["Restart"] = "OmniIconName.Refresh",
            ["Start"] = "OmniIconName.Play",
            ["Stop"] = "OmniIconName.Stop"
        };

    [Fact]
    public void MaterialIconNames_DoNotUseDeprecatedSuffixes()
    {
        var frontRoot = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var violations = RepositoryScan.Enumerate(frontRoot, "*.*")
            .Where(file => file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => DeprecatedIconSuffixes.SelectMany(suffix =>
                Regex.Matches(File.ReadAllText(file), $"\\\"(?<icon>[a-z0-9_]+{Regex.Escape(suffix)})\\\"")
                    .Select(match => $"{Path.GetRelativePath(frontRoot, file)}: {match.Groups["icon"].Value}")))
            .ToList();

        Assert.True(violations.Count == 0,
            "Material icon names must use canonical unsuffixed names:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void LocalizedTextButtons_CarryAnIcon()
    {
        var violations = EnumerateButtons()
            .Where(button => Regex.IsMatch(button.Body, @"@L\[""[^""]+""\]"))
            .Where(button => !button.Body.Contains("<OmniIcon", StringComparison.Ordinal))
            .Select(button => $"{button.File}: localized button has no OmniIcon: {button.OpeningTag}")
            .ToList();

        Assert.True(violations.Count == 0,
            "Every statically localized text button must carry an OmniIcon:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void IconOnlyButtons_HaveAnAccessibleNameAndTooltip()
    {
        var violations = EnumerateButtons()
            .Where(button => button.Body.Contains("<OmniIcon", StringComparison.Ordinal))
            .Where(button => IsIconOnly(button.Body))
            .Where(button => Attribute(button.OpeningTag, "Title") is null
                && Attribute(button.OpeningTag, "Label") is null
                && Attribute(button.OpeningTag, "aria-label") is null)
            .Select(button => $"{button.File}: icon-only button has no Title or Label: {button.OpeningTag}")
            .ToList();

        Assert.True(violations.Count == 0,
            "Every icon-only OmniButton must expose a tooltip or accessible name:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void DataGridButtons_AreIconActions()
    {
        var violations = EnumerateGridButtons()
            .Where(button => !button.Body.Contains("<OmniIcon", StringComparison.Ordinal))
            .Select(button => $"{button.File}: table button has no OmniIcon: {button.OpeningTag}")
            .ToList();

        Assert.True(violations.Count == 0,
            "Every button rendered in a data-grid cell must be an icon action:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void RecurringTableActions_UseCanonicalIcons()
    {
        var violations = new List<string>();
        foreach (var button in EnumerateGridButtons())
        {
            var key = Regex.Match(button.Markup, @"@L\[""(?<key>[^""]+)""\]").Groups["key"].Value;
            if (!CanonicalTableActionIcons.TryGetValue(key, out var expected)) continue;
            if (!button.Body.Contains($"Name=\"{expected}\"", StringComparison.Ordinal))
                violations.Add($"{button.File}: {key} must use {expected}");
        }

        Assert.True(violations.Count == 0,
            "Recurring table actions must use the same canonical icon everywhere:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void DataGridCss_KeepsActionsCompactAndIconOnly()
    {
        var css = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        Assert.Contains(".omni-data-grid tr[data-omni-row-index] .omni-button .omni-button__content", css);
        Assert.Contains("width: var(--aetheus-grid-row-content-height);", css);
        Assert.Contains("min-height: var(--aetheus-grid-row-content-height);", css);
        Assert.Contains("font-size: 0.875rem;", css);
        Assert.Contains("border: 1px solid color-mix", css);
        Assert.Contains("margin-inline: 0.125rem;", css);
        Assert.DoesNotMatch(
            @"(?s)\.omni-data-grid\s+tr\[data-omni-row-index\]\s*>\s*td\s*\{[^}]*display:\s*block",
            css);
    }

    [Fact]
    public void EveryRenderedDataGridButton_ReceivesATooltip()
    {
        var root = RepositoryScan.Root;
        var script = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Front", "wwwroot", "js", "layout.js"));
        var index = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Front", "wwwroot", "index.html"));

        Assert.Contains(".omni-data-grid button", script, StringComparison.Ordinal);
        Assert.Contains("button.querySelector('.omni-button__content')", script, StringComparison.Ordinal);
        Assert.Contains("button.getAttribute('aria-label')", script, StringComparison.Ordinal);
        Assert.Contains("button.setAttribute('title', label)", script, StringComparison.Ordinal);
        Assert.Contains("new MutationObserver", script, StringComparison.Ordinal);
        Assert.Contains("js/layout.js?v=19", index, StringComparison.Ordinal);
    }

    private static IEnumerable<ButtonMarkup> EnumerateButtons()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        foreach (var file in RepositoryScan.Enumerate(front, "*.razor"))
        {
            var source = StripRazorComments(File.ReadAllText(file));
            foreach (var button in ExtractButtons(source))
                yield return button with { File = Path.GetRelativePath(front, file) };
        }
    }

    private static IEnumerable<ButtonMarkup> EnumerateGridButtons()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        foreach (var file in RepositoryScan.Enumerate(front, "*.razor"))
        {
            var source = StripRazorComments(File.ReadAllText(file));
            foreach (Match column in Regex.Matches(source,
                         @"<OmniDataGridColumn\b.*?</OmniDataGridColumn>", RegexOptions.Singleline))
                foreach (var button in ExtractButtons(column.Value))
                    yield return button with { File = Path.GetRelativePath(front, file) };
        }
    }

    private static IEnumerable<ButtonMarkup> ExtractButtons(string source)
    {
        var index = 0;
        while ((index = source.IndexOf("<OmniButton", index, StringComparison.Ordinal)) >= 0)
        {
            var openingEnd = FindTagEnd(source, index);
            if (openingEnd < 0) yield break;
            var opening = source[index..(openingEnd + 1)];
            if (opening.EndsWith("/>", StringComparison.Ordinal))
            {
                index = openingEnd + 1;
                continue;
            }
            var close = source.IndexOf("</OmniButton>", openingEnd + 1, StringComparison.Ordinal);
            if (close < 0) yield break;
            var body = source[(openingEnd + 1)..close];
            yield return new ButtonMarkup(string.Empty, opening, body, source[index..(close + 13)]);
            index = close + 13;
        }
    }

    private static bool IsIconOnly(string body)
    {
        var withoutIcons = Regex.Replace(body, @"<OmniIcon\b(?:(?:""[^""]*"")|[^>])*?/>", string.Empty,
            RegexOptions.Singleline);
        return string.IsNullOrWhiteSpace(Regex.Replace(withoutIcons, @"</?span\b[^>]*>", string.Empty));
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\b{Regex.Escape(name)}\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static int FindTagEnd(string source, int start)
    {
        var inQuote = false;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '"') inQuote = !inQuote;
            else if (source[i] == '>' && !inQuote) return i;
        }
        return -1;
    }

    private static string StripRazorComments(string source) =>
        Regex.Replace(source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

    private sealed record ButtonMarkup(string File, string OpeningTag, string Body, string Markup);
}
