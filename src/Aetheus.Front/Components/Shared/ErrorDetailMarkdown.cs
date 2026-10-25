// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;

namespace Aetheus.Front.Components.Shared;

/// <summary>Writes an error detail as Markdown, ready to paste in a prompt (R-39). The copy is the
/// clipboard only, never a file; every fact the dialog shows is in the text, nothing is shortened.</summary>
public static class ErrorDetailMarkdown
{
    /// <summary>One grouped app error: type, occurrences, first and last seen (UTC, ISO 8601, as the
    /// exports write them), fingerprint, message and the only stack frame the ingestion keeps.</summary>
    public static string ForAppError(AppErrorEventDto error, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(text);
        var markdown = new StringBuilder();
        markdown.Append("## ").AppendLine(text("ErrorDetails")).AppendLine();
        Fact(markdown, text("ErrorType"), error.ExceptionType);
        Fact(markdown, text("Occurrences"), error.OccurrenceCount.ToString(CultureInfo.InvariantCulture));
        Fact(markdown, text("FirstSeen"), AppTelemetryMarkdownExport.IsoUtc(error.FirstSeenAt));
        Fact(markdown, text("LastSeen"), AppTelemetryMarkdownExport.IsoUtc(error.LastSeenAt));
        if (!string.IsNullOrWhiteSpace(error.Fingerprint))
            Fact(markdown, text("ErrorFingerprint"), error.Fingerprint);
        Section(markdown, text("Message"), error.Message);
        Section(markdown, text("ErrorTopFrame"), string.IsNullOrWhiteSpace(error.TopFrame) ? text("ErrorNoTopFrame") : error.TopFrame);
        return markdown.ToString().TrimEnd() + "\n";
    }

    /// <summary>One log entry opened from a log grid: its message, then the exception details when it has some.</summary>
    public static string ForLogEntry(string message, string? exception, string exceptionTitle, Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var markdown = new StringBuilder();
        markdown.Append("## ").AppendLine(text("Message")).AppendLine();
        Block(markdown, message);
        if (!string.IsNullOrWhiteSpace(exception))
            Section(markdown, exceptionTitle, exception);
        return markdown.ToString().TrimEnd() + "\n";
    }

    private static void Fact(StringBuilder markdown, string label, string value) =>
        markdown.Append("- **").Append(label).Append("**: `").Append(value.Replace('`', '\'')).AppendLine("`");

    private static void Section(StringBuilder markdown, string title, string content)
    {
        markdown.AppendLine().Append("### ").AppendLine(title).AppendLine();
        Block(markdown, content);
    }

    // The fence is one backtick longer than the longest run in the content, so the content can never close it.
    private static void Block(StringBuilder markdown, string content)
    {
        var fence = new string('`', Math.Max(3, LongestBacktickRun(content) + 1));
        markdown.AppendLine(fence).AppendLine(content).AppendLine(fence);
    }

    private static int LongestBacktickRun(string content)
    {
        int longest = 0, run = 0;
        foreach (var character in content)
        {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }
}
