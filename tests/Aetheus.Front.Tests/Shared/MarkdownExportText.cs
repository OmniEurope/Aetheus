// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// OE 1.14 writes every data text of a Markdown export inert (OmniTableExporter, audit of 2026-11-13): the ASCII
/// punctuation Markdown reads is escaped with a backslash, and <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> become
/// character references. <see cref="AsRead"/> gives the text back as a Markdown viewer shows it, so a test asserts
/// what the reader sees; the tests that assert the escaping itself read the raw file.
/// </summary>
internal static class MarkdownExportText
{
    private const string AsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    public static string AsRead(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var read = new StringBuilder(markdown.Length);
        for (var index = 0; index < markdown.Length; index++)
        {
            if (markdown[index] == '\\' && index + 1 < markdown.Length && AsciiPunctuation.Contains(markdown[index + 1], StringComparison.Ordinal))
                index++;
            read.Append(markdown[index]);
        }
        // References last: an escaped "\&" read above stays a plain "&", as a viewer shows it.
        return read.ToString()
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }
}
