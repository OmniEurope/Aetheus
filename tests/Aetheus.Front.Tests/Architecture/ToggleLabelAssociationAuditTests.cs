// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Switches require content or ARIA labels unless they are decorative, keyboard-inert
/// mirrors inside named clickable wrappers.</summary>
public class ToggleLabelAssociationAuditTests
{
    [Fact]
    public void RawOmniSwitches_HaveAnAccessibleNameOrAreDecorative()
    {
        var violations = new List<string>();
        var razorDir = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

        foreach (var file in RepositoryScan.Enumerate(razorDir, "*.razor"))
        {
            var source = StripRazorComments(File.ReadAllText(file));
            foreach (Match match in Regex.Matches(
                         source,
                         @"<OmniSwitch\b(?:(?:""[^""]*"")|[^>])*?/?>",
                         RegexOptions.Singleline))
            {
                var tag = match.Value;
                var named = tag.Contains("aria-label=", StringComparison.OrdinalIgnoreCase)
                    || tag.Contains("aria-labelledby=", StringComparison.OrdinalIgnoreCase)
                    || NamedByContent(source, match);
                var decorative = tag.Contains("aria-hidden=\"true\"", StringComparison.OrdinalIgnoreCase)
                    && tag.Contains("tabindex=\"-1\"", StringComparison.OrdinalIgnoreCase)
                    && tag.Contains("Disabled=\"true\"", StringComparison.OrdinalIgnoreCase);
                if (!named && !decorative)
                    violations.Add($"{Path.GetRelativePath(razorDir, file)}: {tag}");
            }
        }

        Assert.True(violations.Count == 0,
            "Raw OmniSwitch controls must expose aria-label/aria-labelledby or be decorative and inert:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void NativeRadios_HaveAnAccessibleClickableLabel()
    {
        var violations = new List<string>();
        var razorDir = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var radioPattern = new Regex(
            """<input\b(?=[^>]*\btype\s*=\s*["']radio["'])[^>]*>""",
            RegexOptions.IgnoreCase);

        foreach (var file in RepositoryScan.Enumerate(razorDir, "*.razor"))
        {
            var source = StripRazorComments(File.ReadAllText(file));
            foreach (Match match in radioPattern.Matches(source))
            {
                var explicitlyNamed = match.Value.Contains("aria-label=", StringComparison.OrdinalIgnoreCase)
                    || match.Value.Contains("aria-labelledby=", StringComparison.OrdinalIgnoreCase);
                if (!explicitlyNamed && !WrappedByHtmlLabel(source, match.Index))
                    violations.Add($"{Path.GetRelativePath(razorDir, file)}: native radio has no accessible label");
            }
        }

        Assert.True(violations.Count == 0,
            "Radio controls must have an accessible clickable label:\n  " + string.Join("\n  ", violations));
    }

    private static bool WrappedByHtmlLabel(string source, int controlStart)
    {
        var open = source.LastIndexOf("<label", controlStart, StringComparison.OrdinalIgnoreCase);
        if (open < 0) return false;
        var close = source.LastIndexOf("</label>", controlStart, StringComparison.OrdinalIgnoreCase);
        return close < open;
    }

    /// <summary>
    /// True when the switch is not self-closing and text or a Razor expression is left between its
    /// tags once the markup is stripped: that content is the switch's name.
    /// </summary>
    private static bool NamedByContent(string source, Match openingTag)
    {
        if (openingTag.Value.EndsWith("/>", StringComparison.Ordinal))
            return false;

        var start = openingTag.Index + openingTag.Length;
        var end = source.IndexOf("</OmniSwitch>", start, StringComparison.Ordinal);
        if (end < 0)
            return false;

        return !string.IsNullOrWhiteSpace(Regex.Replace(source[start..end], "<[^>]*>", string.Empty));
    }

    private static string StripRazorComments(string source) =>
        Regex.Replace(source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
}
