// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary> The passage of a source file a finding points at, shown under its location: the lines
/// of the finding and a few lines around them, never the whole file. <see cref="FirstLine"/> and
/// <see cref="LastLine"/> are the file's own line numbers, and so are <see
/// cref="HighlightedLines"/>, the lines of the finding: OE's OmniCodeViewer numbers the passage
/// from <see cref="FirstLine"/>. </summary>
internal sealed record SourceExcerpt(int FirstLine, int LastLine, string Code, IReadOnlyList<int> HighlightedLines)
{
    /// <summary>Lines shown before and after the finding's own lines.</summary>
    internal const int ContextLines = 3;

    /// <summary>At most this many lines of the finding are shown; a longer range is cut.</summary>
    internal const int MaxFindingLines = 40;

    /// <summary>The passage for lines <paramref name="startLine"/> to <paramref name="endLine"/>
    /// (1-based), or null when the file does not have that line.</summary>
    internal static SourceExcerpt? Cut(string content, int startLine, int? endLine)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (startLine < 1 || startLine > lines.Length) return null;

        var end = Math.Clamp(endLine ?? startLine, startLine, Math.Min(lines.Length, startLine + MaxFindingLines - 1));
        var first = Math.Max(1, startLine - ContextLines);
        var last = Math.Min(lines.Length, end + ContextLines);
        var code = string.Join('\n', lines[(first - 1)..last]);
        var highlighted = Enumerable.Range(startLine, end - startLine + 1).ToArray();
        return new SourceExcerpt(first, last, code, highlighted);
    }
}
