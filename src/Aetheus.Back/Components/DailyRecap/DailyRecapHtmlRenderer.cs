// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Services.Email;
using static Aetheus.Back.Services.Email.EmailHtmlLayout;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// What the daily recap template (recette R1-75) is rendered with: the day, the period and the settings
/// link as values, the tiles and the sections as blocks (HTML fragments the template places). The mail
/// itself, its frame, title and footer, is the template's. Status badges carry their words as well as their
/// colour; lines between rows only, never a coloured band down one side of a block.
/// </summary>
internal static class DailyRecapHtmlRenderer
{
    public static MailTemplateData Data(RecapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DailyRecapMailVariables.Date] = document.Date,
            [DailyRecapMailVariables.Period] = document.Intro,
            [DailyRecapMailVariables.SettingsUrl] = document.SettingsLink?.Url ?? string.Empty
        };
        var blocks = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DailyRecapMailVariables.Tiles] = EmailHtmlLayout.Tiles(
                [.. document.Tiles.Select(tile => (tile.Value, tile.Label, Colours(tile.Tone).Background, Colours(tile.Tone).Ink))]),
            [DailyRecapMailVariables.Sections] = Sections(document)
        };
        return new MailTemplateData(values, blocks);
    }

    private static string Sections(RecapDocument document)
    {
        var html = new StringBuilder(8 * 1024);
        foreach (var block in document.Blocks)
        {
            Section(html, block.Title, block.Summary);
            if (block.Items.Count > 0)
            {
                html.Append("<table>");
                foreach (var item in block.Items) Item(html, item, document);
                html.Append("</table>");
            }
            if (!string.IsNullOrWhiteSpace(block.Note))
                html.Append("<p style=\"font-size:12px;color:").Append(Muted).Append("\">").Append(Encode(block.Note)).Append("</p>");
        }
        return html.ToString();
    }

    private static void Item(StringBuilder html, RecapItem item, RecapDocument document)
    {
        ItemRow(html);
        if (item.Badge is { } badge) Badge(html, badge).Append(' ');
        html.Append("<strong>").Append(Encode(item.Title)).Append("</strong>");
        if (!string.IsNullOrWhiteSpace(item.Subtitle))
            html.Append("<br><span style=\"font-size:13px;color:").Append(Muted).Append("\">").Append(Encode(item.Subtitle)).Append("</span>");
        foreach (var line in item.Lines)
        {
            html.Append("<br><span style=\"font-size:13px;color:").Append(Muted).Append("\">")
                .Append(Encode(line.Label + document.LabelSeparator)).Append("</span> ");
            if (line.Badge is { } lineBadge) Badge(html, lineBadge).Append(' ');
            html.Append("<span style=\"font-size:13px;color:").Append(Ink).Append("\">").Append(Encode(line.Text)).Append("</span>");
        }
        if (item.Link is { } link)
            html.Append("<br><a href=\"").Append(Encode(link.Url)).Append("\" style=\"font-size:13px;font-weight:600\">")
                .Append(Encode(link.Text)).Append("</a>");
        html.Append("</td></tr>");
    }

    private static StringBuilder Badge(StringBuilder html, RecapBadge badge)
    {
        var (background, ink) = Colours(badge.Tone);
        return EmailHtmlLayout.Badge(html, background, ink, badge.Text);
    }

    /// <summary>Light fill, dark text of the same hue: readable, and AA contrast on every pair.</summary>
    internal static (string Background, string Ink) Colours(RecapTone tone) => tone switch
    {
        RecapTone.Success => ("#e3f4e8", "#17643a"),
        RecapTone.Danger => ("#fde8e6", "#a3231a"),
        RecapTone.Warning => ("#fff3d6", "#7a4b00"),
        _ => ("#eef0f3", "#404b5a")
    };
}
