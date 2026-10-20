// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Masks C# comments and literals while preserving offsets and line breaks so source
/// guards only inspect code.</summary>
internal static class SourceMasker
{
    internal static string MaskCommentsAndStrings(string source)
    {
        var masked = new StringBuilder(source);
        var index = 0;
        while (index < source.Length)
        {
            var end = EndOfCommentOrLiteral(source, index);
            if (end == index)
            {
                index++;
                continue;
            }

            Blank(masked, index, end);
            index = end;
        }
        return masked.ToString();
    }

    /// <summary>The exclusive end of the comment or literal that starts at <paramref name="index"/>,
    /// or <paramref name="index"/> itself when none starts there.</summary>
    private static int EndOfCommentOrLiteral(string source, int index)
    {
        if (StartsAt(source, index, "//")) return EndOfLineComment(source, index + 2);
        if (StartsAt(source, index, "/*")) return EndOfBlockComment(source, index + 2);
        return source[index] is '"' or '\'' ? EndOfLiteral(source, index) : index;
    }

    private static bool StartsAt(string source, int index, string token) =>
        string.CompareOrdinal(source, index, token, 0, token.Length) == 0;

    private static int EndOfLineComment(string source, int from)
    {
        var end = source.IndexOf('\n', from);
        return end < 0 ? source.Length : end;
    }

    private static int EndOfBlockComment(string source, int from)
    {
        var end = source.IndexOf("*/", from, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + 2;
    }

    /// <summary>A verbatim string doubles its quote to escape it; any other literal escapes with a backslash.</summary>
    private static int EndOfLiteral(string source, int start)
    {
        var delimiter = source[start];
        var verbatim = delimiter == '"' && start > 0 && source[start - 1] == '@';
        var end = start + 1;
        while (end < source.Length)
        {
            if (source[end] == delimiter)
            {
                if (!verbatim || end + 1 >= source.Length || source[end + 1] != '"') return end + 1;
                end += 2;
                continue;
            }

            end += !verbatim && source[end] == '\\' && end + 1 < source.Length ? 2 : 1;
        }
        return end;
    }

    private static void Blank(StringBuilder source, int start, int exclusiveEnd)
    {
        for (var i = start; i < exclusiveEnd; i++)
            if (source[i] is not ('\r' or '\n')) source[i] = ' ';
    }
}
