// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R-48: the few last lines certbot printed during a certificate deletion, kept short enough to sit in the
/// "reason" of a failed resource. The agent's own bookkeeping lines (<c>[process] ...</c>) are dropped, the
/// <c>[stderr]</c> prefix is removed, a line that looks like a secret is never kept, and the whole excerpt is bounded.
/// </summary>
internal static partial class CertbotOutputExcerpt
{
    internal const int MaxLines = 6;
    internal const int MaxLineLength = 160;
    internal const int MaxLength = 500;

    public static string Of(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var kept = lines
            .Select(Clean)
            .Where(line => line.Length > 0 && !line.StartsWith("[process]", StringComparison.Ordinal) && !SecretLike().IsMatch(line))
            .TakeLast(MaxLines)
            .Select(line => line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line)
            .ToList();
        var text = string.Join(" | ", kept);
        return text.Length > MaxLength ? "..." + text[^MaxLength..] : text;
    }

    private static string Clean(string raw)
    {
        var line = raw.Trim();
        if (line.StartsWith("[stderr]", StringComparison.Ordinal)) line = line["[stderr]".Length..].Trim();
        var builder = new StringBuilder(line.Length);
        foreach (var character in line) builder.Append(char.IsControl(character) ? ' ' : character);
        return builder.ToString().Trim();
    }

    [GeneratedRegex(@"(?i)(token|secret|passw|api[_-]?key|credential|authorization|bearer)")]
    private static partial Regex SecretLike();
}
