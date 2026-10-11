// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>The plain-text part of the recap, for clients that do not show HTML: the same document, with its badges in brackets.</summary>
internal static class DailyRecapTextRenderer
{
    public static string Render(RecapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder(4 * 1024);
        text.AppendLine(document.Heading).AppendLine(new string('=', Math.Min(72, document.Heading.Length)))
            .AppendLine(document.Intro).AppendLine();
        foreach (var tile in document.Tiles)
            text.Append("- ").Append(tile.Label).Append(document.LabelSeparator).Append(' ').AppendLine(tile.Value);
        if (document.Tiles.Count > 0) text.AppendLine();

        foreach (var block in document.Blocks)
        {
            text.AppendLine(block.Title).AppendLine(new string('-', Math.Min(72, block.Title.Length))).AppendLine(block.Summary);
            foreach (var item in block.Items)
            {
                text.Append("* ");
                if (item.Badge is { } badge) text.Append('[').Append(badge.Text).Append("] ");
                text.Append(item.Title);
                if (!string.IsNullOrWhiteSpace(item.Subtitle)) text.Append(" (").Append(item.Subtitle).Append(')');
                text.AppendLine();
                foreach (var line in item.Lines)
                {
                    text.Append("    ").Append(line.Label).Append(document.LabelSeparator).Append(' ');
                    if (line.Badge is { } lineBadge) text.Append('[').Append(lineBadge.Text).Append("] ");
                    text.AppendLine(line.Text.TrimEnd());
                }
            }
            if (!string.IsNullOrWhiteSpace(block.Note)) text.AppendLine(block.Note);
            text.AppendLine();
        }
        text.AppendLine(document.Footer);
        if (document.SettingsLink is { } link) text.Append(link.Text).Append(document.LabelSeparator).Append(' ').AppendLine(link.Url);
        return text.ToString();
    }
}
