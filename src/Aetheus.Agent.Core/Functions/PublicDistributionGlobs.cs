// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The glob dialect of <c>boundary.json</c>, port of <c>globToRegExp</c> and <c>matchesAny</c> of
/// <c>scripts/export-public-distribution.mjs</c>: a double star crosses directories, a single
/// star stays inside one segment, and a leading <c>**/</c> also matches the root. Every other character
/// is taken as the script's regular expression took it (a <c>?</c> stays a quantifier), so a glob
/// selects the same paths. The expressions are built once per list, where the script rebuilt them for
/// every path.
/// </summary>
internal sealed class PublicDistributionGlobs
{
    private const string EscapedCharacters = ".+^${}()|[]\\";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(10);

    private readonly Regex[] _patterns;

    public PublicDistributionGlobs(IEnumerable<string> globs)
    {
        ArgumentNullException.ThrowIfNull(globs);
        _patterns = [.. globs.Select(ToRegex)];
    }

    /// <summary><c>matchesAny(path, globs)</c>.</summary>
    public bool IsMatch(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _patterns.Any(pattern => pattern.IsMatch(path));
    }

    /// <summary><c>matchesAny(path, globs)</c> for a one-off list.</summary>
    public static bool MatchesAny(string path, IEnumerable<string> globs) => new PublicDistributionGlobs(globs).IsMatch(path);

    /// <summary><c>globToRegExp(glob)</c>: <c>^(?:.*/)?body$</c> with the end anchored strictly.</summary>
    internal static Regex ToRegex(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var pattern = JsValue.Trim(glob);
        var anyDirectory = pattern.StartsWith("**/", StringComparison.Ordinal);
        if (anyDirectory)
            pattern = pattern[3..];
        var body = string.Join(".*", pattern.Split("**").Select(TranslateSegment));
        return new Regex($"^{(anyDirectory ? "(?:.*/)?" : string.Empty)}{body}\\z", RegexOptions.CultureInvariant, MatchTimeout);
    }

    /// <summary>The text between double stars: special characters escaped, a single star confined to one segment.</summary>
    private static string TranslateSegment(string part)
    {
        var builder = new StringBuilder(part.Length + 8);
        foreach (var character in part)
        {
            if (character == '*')
                builder.Append("[^/]*");
            else
            {
                if (EscapedCharacters.Contains(character, StringComparison.Ordinal))
                    builder.Append('\\');
                builder.Append(character);
            }
        }
        return builder.ToString();
    }
}
