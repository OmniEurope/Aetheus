// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Opaque identifiers use ShortId and an object destination unless explicitly excepted.
/// Null runtime destinations remain plain text; title attributes and badge content retain their
/// documented allowances.</summary>
public sealed class ShortIdentifierLinkAuditTests
{
    /// <summary>File name and Value expression of a ShortId allowed without Href, with the reason.</summary>
    private static readonly Dictionary<(string File, string Value), string> WithoutHref = new()
    {
        [("ArtifactDetail.razor", "@release.Version")] =
            "Inside the enclosing link to the release, which also wraps its status badge; a link cannot nest.",
        [("PipelineRun.razor", "@release.Version")] =
            "Inside the release tile, which is itself the link to the release; a link cannot nest.",
        [("ReleasePickerGrid.razor", "@release.Version")] =
            "A picker cell: clicking the row fills the version field, a link would leave the launch form."
    };

    /// <summary>The shortening helpers, and a hand-made cut of a hash (<c>Sha[..7]</c>), which escaped the
    /// rule with seven characters and no link until the 2026-09-29 survey (branches and tags of a
    /// repository, the runs' commit columns, the analysis portfolio).</summary>
    private static readonly Regex ShortCall = new(
        @"\bShort(?:Sha|Version|Commit)\(|(?:Sha|Hash|Digest|Sha256)\??\[\.\.",
        RegexOptions.Compiled);
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ShortIdTag = new(@"<ShortId\b(?<attrs>[^>]*?)/?>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ValueAttribute = new(@"\bValue=""(?<value>[^""]*)""", RegexOptions.Compiled);

    [Fact]
    public void NoPage_RendersAShortenedIdentifierAsText()
    {
        var violations = new List<string>();
        foreach (var (root, file) in RepositoryScan.EnumerateUnion(RepositoryScan.PageRoots, "*.razor"))
        {
            if (Path.GetFileName(file) == "ShortId.razor") continue;
            var text = WithoutComments(File.ReadAllText(file));
            foreach (Match match in ShortCall.Matches(text))
            {
                if (!IsInsideTag(text, match.Index) && !IsBadgeText(text, match.Index))
                    violations.Add($"{Path.GetRelativePath(root, file)}:{LineOf(text, match.Index)}");
            }
        }

        Assert.True(violations.Count == 0,
            "Un identifiant long affiché comme texte doit passer par <ShortId Value=... Href=...> (recette R-373), "
            + "pas par ShortSha(...) ou ShortVersion(...) :\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void EveryShortId_LinksToItsObject_UnlessListedWithAReason()
    {
        var violations = new List<string>();
        var used = new HashSet<(string File, string Value)>();
        var tags = 0;
        foreach (var (root, file) in RepositoryScan.EnumerateUnion(RepositoryScan.PageRoots, "*.razor"))
        {
            var name = Path.GetFileName(file);
            if (name == "ShortId.razor") continue;
            var text = WithoutComments(File.ReadAllText(file));
            foreach (Match tag in ShortIdTag.Matches(text))
            {
                tags++;
                var attrs = tag.Groups["attrs"].Value;
                if (Regex.IsMatch(attrs, @"\bHref=")) continue;
                var key = (name, ValueAttribute.Match(attrs).Groups["value"].Value);
                if (WithoutHref.ContainsKey(key))
                {
                    used.Add(key);
                    continue;
                }
                violations.Add($"{Path.GetRelativePath(root, file)}:{LineOf(text, tag.Index)}: {tag.Value.Trim()}");
            }
        }

        Assert.True(tags > 0, "Aucune balise <ShortId> trouvée : le relevé ne protège rien.");
        var stale = WithoutHref.Keys.Where(key => !used.Contains(key)).Select(key => $"{key.File} {key.Value}").ToList();
        Assert.True(violations.Count == 0,
            "Chaque <ShortId> doit déclarer Href vers son objet (recette R-373). Une exception se justifie dans "
            + "WithoutHref :\n  " + string.Join("\n  ", violations));
        Assert.True(stale.Count == 0,
            "Exceptions sans usage, à retirer de WithoutHref :\n  " + string.Join("\n  ", stale));
    }

    private static string WithoutComments(string text) =>
        RazorComment.Replace(text, comment => new string(comment.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));

    /// <summary>True when the position sits between a tag's opening <c>&lt;</c> and its closing
    /// <c>&gt;</c>, i.e. in an attribute value. A <c>=&gt;</c> lambda arrow is not a tag end.</summary>
    private static bool IsInsideTag(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (text[i] == '<') return true;
            if (text[i] == '>' && (i == 0 || text[i - 1] != '=')) return false;
        }
        return false;
    }

    /// <summary>Identifies text inside an OmniBadge or a NumberBadge (the badge of a text that carries a
    /// number, such as a version), where short non-link labels are allowed.</summary>
    private static bool IsBadgeText(string text, int index)
    {
        var tag = text.LastIndexOf("<NumberBadge", index, StringComparison.Ordinal) > text.LastIndexOf("<OmniBadge", index, StringComparison.Ordinal)
            ? "NumberBadge"
            : "OmniBadge";
        var open = text.LastIndexOf($"<{tag}", index, StringComparison.Ordinal);
        if (open < 0 || text.IndexOf($"</{tag}>", open, index - open, StringComparison.Ordinal) >= 0) return false;
        // The opening tag ends at its first '>' that is not a lambda arrow; a self-closed badge has no content.
        var end = open;
        while (end < index && (text[end] != '>' || text[end - 1] == '=')) end++;
        return end < index && text[end - 1] != '/';
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
